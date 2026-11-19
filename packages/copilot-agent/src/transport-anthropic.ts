// The Claude transport: builds the streaming Messages request and reports text as it arrives.
//
// Request rules (see the Claude API reference; all asserted by transport-anthropic.test.ts):
// - adaptive thinking only, depth controlled by `output_config.effort`, which is always explicit;
//   no temperature/top_p/top_k, no assistant prefill, `tool_choice` auto only;
// - streamed with `max_tokens` 64000 and collected with the SDK's `finalMessage()`;
// - prompt caching: a breakpoint on the stable system block and on the last tool definition, plus
//   automatic caching of the growing conversation tail (the top-level `cache_control`);
// - server-side refusal fallback (`fallbacks: "default"`) on the models that support it;
// - tools are sent without `strict`: the catalog schemas use numeric, length and list limits that
//   strict mode does not accept, and strict mode is unavailable to some organisations. They are
//   sent with `eager_input_streaming` (a streamed request with client tools), so the API no longer
//   validates tool inputs: the session validates every input before running a tool, never runs the
//   tools of a turn cut off by `max_tokens` or a refusal, and this transport re-asks when the SDK
//   cannot parse a tool input at all.
import { APIError, Anthropic } from '@anthropic-ai/sdk';
import type {
  BetaMessage,
  BetaMessageStreamParams,
  BetaRawMessageStreamEvent,
  BetaTool,
  BetaUsage,
} from '@anthropic-ai/sdk/resources/beta/messages/messages';
import { describeModelError } from './errors';
import type { ModelId, ModelRequest, ModelResponse, ModelRunHooks, ModelTransport, TurnUsage } from './types';

/** Output cap of one model call. Thinking counts towards it, so it is sized for long agentic turns. */
export const MAX_OUTPUT_TOKENS = 64_000;
/** Beta flag of the server-side refusal fallback (the scalar `"default"` form needs this version). */
export const FALLBACK_BETA = 'server-side-fallback-2026-07-01';
/** Models with a server-side refusal fallback. Claude Haiku 5.5 has none. */
const FALLBACK_MODELS: ReadonlySet<ModelId> = new Set<ModelId>([
  'claude-opus-5-5',
  'claude-fable-5-2',
  'claude-sonnet-5-5',
]);
/** How often one model call is re-asked when the SDK cannot read the response (not for API errors). */
const MAX_UNREADABLE_RETRIES = 2;

/** True when a declined request on this model can be retried server-side on a fallback model. */
export function supportsServerFallback(model: ModelId): boolean {
  return FALLBACK_MODELS.has(model);
}

/**
 * Builds the body of the streaming Messages request for one model call. Pure and deterministic:
 * the same request always yields the same body, which keeps the cached prefix stable.
 */
export function buildAnthropicRequest(request: ModelRequest): BetaMessageStreamParams {
  const lastTool = request.tools.length - 1;
  const tools: BetaTool[] = request.tools.map((tool, index) => {
    const definition: BetaTool = {
      name: tool.name,
      description: tool.description,
      input_schema: { ...tool.inputSchema, type: 'object' },
      eager_input_streaming: true,
    };
    if (index === lastTool) definition.cache_control = { type: 'ephemeral' };
    return definition;
  });

  const body: BetaMessageStreamParams = {
    model: request.model,
    max_tokens: MAX_OUTPUT_TOKENS,
    system: [{ type: 'text', text: request.system, cache_control: { type: 'ephemeral' } }],
    tools,
    tool_choice: { type: 'auto' },
    thinking: { type: 'adaptive' },
    output_config: { effort: request.effort },
    cache_control: { type: 'ephemeral' },
    messages: [...request.messages],
  };
  if (supportsServerFallback(request.model)) {
    body.betas = [FALLBACK_BETA];
    body.fallbacks = 'default';
  }
  return body;
}

/** The part of the SDK's message stream this transport uses. */
export interface AnthropicStreamLike {
  on(event: 'streamEvent', listener: (event: BetaRawMessageStreamEvent) => void): unknown;
  finalMessage(): Promise<BetaMessage>;
  /** Cancels the underlying request (used to drop an attempt whose response could not be read). */
  abort?(): void;
}

/** The part of the SDK client this transport uses (lets tests stub the client without HTTP). */
export interface AnthropicClientLike {
  beta: {
    messages: {
      stream(body: BetaMessageStreamParams, options?: { signal?: AbortSignal }): AnthropicStreamLike;
    };
  };
}

/** Options of {@link createAnthropicTransport}. */
export interface AnthropicTransportOptions {
  /** The Anthropic API key. Ignored when `client` is given. */
  apiKey?: string;
  /** A ready client (or a stub in tests). */
  client?: AnthropicClientLike;
}

function count(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

/**
 * Token usage of one response. When a fallback model took over, the top-level usage covers only
 * the attempt that produced the message, so the per-attempt `iterations` are summed instead.
 */
export function usageFromMessage(usage: BetaUsage | null | undefined): TurnUsage {
  const total: TurnUsage = { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 };
  if (usage === null || usage === undefined) return total;
  const iterations: readonly unknown[] = Array.isArray(usage.iterations) ? usage.iterations : [];
  const parts: readonly unknown[] = iterations.length > 0 ? iterations : [usage];
  for (const part of parts) {
    if (typeof part !== 'object' || part === null) continue;
    const entry = part as Record<string, unknown>;
    total.inputTokens += count(entry.input_tokens);
    total.outputTokens += count(entry.output_tokens);
    total.cacheReadTokens += count(entry.cache_read_input_tokens);
    total.cacheCreationTokens += count(entry.cache_creation_input_tokens);
  }
  return total;
}

function toResponse(message: BetaMessage): ModelResponse {
  const response: ModelResponse = {
    content: message.content,
    stopReason: message.stop_reason,
    usage: usageFromMessage(message.usage),
    model: message.model,
  };
  // `stop_details` is only meaningful for a refusal; it may be null even then.
  if (message.stop_reason === 'refusal') {
    response.refusal = {
      category: message.stop_details?.category ?? null,
      explanation: message.stop_details?.explanation ?? null,
    };
  }
  return response;
}

/**
 * The transport that talks to Claude through the official SDK. Give it an API key, or a client
 * (tests pass a stub). Text deltas are reported with the index of their content block.
 */
export function createAnthropicTransport(options: AnthropicTransportOptions): ModelTransport {
  let client = options.client;
  if (client === undefined) {
    if (typeof options.apiKey !== 'string' || options.apiKey.trim().length === 0) {
      throw new Error('createAnthropicTransport needs an API key or a client');
    }
    client = new Anthropic({ apiKey: options.apiKey.trim() });
  }
  const api = client;

  return {
    kind: 'anthropic',
    async run(request: ModelRequest, hooks: ModelRunHooks): Promise<ModelResponse> {
      const body = buildAnthropicRequest(request);
      for (let attempt = 0; ; attempt += 1) {
        let stream: AnthropicStreamLike | undefined;
        try {
          stream = api.beta.messages.stream(body, { signal: hooks.signal });
          stream.on('streamEvent', (event) => {
            if (event.type === 'content_block_delta' && event.delta.type === 'text_delta') {
              hooks.onTextDelta(event.delta.text, event.index);
            }
          });
          // finalMessage() settles for every outcome (completion, API error, abort), and with
          // eager input streaming it also rejects when a tool input was not parseable JSON.
          return toResponse(await stream.finalMessage());
        } catch (error) {
          // API errors (including aborts) are never retried here: the SDK already retried what is
          // retryable. Anything else means the response could not be read; ask again a few times.
          if (error instanceof APIError || hooks.signal.aborted || attempt >= MAX_UNREADABLE_RETRIES) throw error;
          try {
            stream?.abort?.();
          } catch {
            // The attempt is abandoned either way.
          }
          hooks.onRestart?.();
        }
      }
    },
  };
}

/** The part of the SDK client {@link verifyApiKey} uses. */
export interface ModelsClientLike {
  models: { list(params: { limit: number }): PromiseLike<unknown> };
}

/**
 * Checks an API key with one free Models API call. Never throws: failures come back as
 * `{ ok: false, message }` in plain English. `deps.client` replaces the SDK client in tests.
 */
export async function verifyApiKey(
  apiKey: string,
  deps: { client?: ModelsClientLike } = {},
): Promise<{ ok: true } | { ok: false; message: string }> {
  const key = typeof apiKey === 'string' ? apiKey.trim() : '';
  if (key.length === 0) return { ok: false, message: 'Enter an Anthropic API key.' };
  try {
    const client: ModelsClientLike = deps.client ?? new Anthropic({ apiKey: key, maxRetries: 1, timeout: 20_000 });
    await client.models.list({ limit: 1 });
    return { ok: true };
  } catch (error) {
    return { ok: false, message: describeModelError(error).message };
  }
}
