import { describe, it, expect } from 'vitest';
import { APIConnectionError, AnthropicError, AuthenticationError, RateLimitError } from '@anthropic-ai/sdk';
import type {
  BetaMessage,
  BetaMessageStreamParams,
  BetaRawMessageStreamEvent,
} from '@anthropic-ai/sdk/resources/beta/messages/messages';
import { SYSTEM_PROMPT } from './prompt';
import { CopilotSession } from './session';
import { FAKE_SOURCE, fakeExecutor, historyProblems } from './test-support';
import { buildModelTools } from './tools';
import {
  FALLBACK_BETA,
  MAX_OUTPUT_TOKENS,
  buildAnthropicRequest,
  createAnthropicTransport,
  supportsServerFallback,
  usageFromMessage,
  verifyApiKey,
} from './transport-anthropic';
import type { AnthropicClientLike, AnthropicStreamLike } from './transport-anthropic';
import { EFFORTS, MODEL_IDS } from './types';
import type { ModelId, ModelRequest, SessionEvent } from './types';

const headers = new Headers();

function message(overrides: Partial<BetaMessage> = {}): BetaMessage {
  return {
    id: 'msg_1',
    type: 'message',
    role: 'assistant',
    model: 'claude-opus-5-5',
    content: [{ type: 'text', text: 'Hello.', citations: null }],
    stop_reason: 'end_turn',
    stop_sequence: null,
    stop_details: null,
    container: null,
    context_management: null,
    diagnostics: null,
    usage: {
      input_tokens: 11,
      output_tokens: 5,
      cache_read_input_tokens: 900,
      cache_creation_input_tokens: 30,
    } as BetaMessage['usage'],
    ...overrides,
  } as BetaMessage;
}

function textDelta(index: number, text: string): BetaRawMessageStreamEvent {
  return { type: 'content_block_delta', index, delta: { type: 'text_delta', text } };
}

type Outcome = { events?: BetaRawMessageStreamEvent[]; message?: BetaMessage; error?: unknown };

/** A stub of the SDK client: records request bodies and options, replays canned streams. No HTTP. */
function stubClient(outcomes: Outcome[]) {
  const bodies: BetaMessageStreamParams[] = [];
  const options: ({ signal?: AbortSignal } | undefined)[] = [];
  let aborted = 0;
  const client: AnthropicClientLike = {
    beta: {
      messages: {
        stream(body, requestOptions) {
          bodies.push(body);
          options.push(requestOptions);
          const outcome = outcomes[bodies.length - 1] ?? { message: message() };
          let listener: ((event: BetaRawMessageStreamEvent) => void) | undefined;
          const stream: AnthropicStreamLike = {
            on(_event, callback) {
              listener = callback;
              return stream;
            },
            async finalMessage() {
              await Promise.resolve();
              for (const event of outcome.events ?? []) listener?.(event);
              if (outcome.error !== undefined) throw outcome.error;
              return outcome.message ?? message();
            },
            abort() {
              aborted += 1;
            },
          };
          return stream;
        },
      },
    },
  };
  return { client, bodies, options, abortedStreams: () => aborted };
}

function request(model: ModelId = 'claude-opus-5-5', overrides: Partial<ModelRequest> = {}): ModelRequest {
  return {
    model,
    effort: 'high',
    system: SYSTEM_PROMPT,
    tools: buildModelTools(FAKE_SOURCE, ['get_info', 'draw_line']),
    messages: [{ role: 'user', content: [{ type: 'text', text: 'Hi' }] }],
    ...overrides,
  };
}

const hooks = (signal: AbortSignal = new AbortController().signal) => {
  const deltas: { text: string; index: number | undefined }[] = [];
  let restarts = 0;
  return {
    deltas,
    restarts: () => restarts,
    value: {
      signal,
      onTextDelta: (text: string, index?: number) => {
        deltas.push({ text, index });
      },
      onRestart: () => {
        restarts += 1;
      },
    },
  };
};

describe('buildAnthropicRequest', () => {
  for (const model of MODEL_IDS) {
    it(`builds a valid request for ${model}`, () => {
      const body = buildAnthropicRequest(request(model, { effort: 'xhigh' }));

      expect(body.model).toBe(model);
      expect(body.max_tokens).toBe(64000);
      expect(MAX_OUTPUT_TOKENS).toBe(64000);
      // Adaptive thinking only: no budget, never disabled.
      expect(body.thinking).toEqual({ type: 'adaptive' });
      // Effort is always explicit and lives in output_config.
      expect(body.output_config).toEqual({ effort: 'xhigh' });
      // Only automatic tool choice is accepted by these models.
      expect(body.tool_choice).toEqual({ type: 'auto' });
      // Sampling parameters are not accepted.
      expect(body).not.toHaveProperty('temperature');
      expect(body).not.toHaveProperty('top_p');
      expect(body).not.toHaveProperty('top_k');
      expect(body).not.toHaveProperty('stop_sequences');
      // No assistant prefill: the conversation ends with a user message.
      expect(body.messages.at(-1)?.role).toBe('user');

      // Prompt caching: the stable system block, the last tool, and the conversation tail.
      expect(body.system).toEqual([{ type: 'text', text: SYSTEM_PROMPT, cache_control: { type: 'ephemeral' } }]);
      const tools = body.tools ?? [];
      expect(tools.map((tool) => ('name' in tool ? tool.name : ''))).toEqual(['get_info', 'draw_line', 'ask_user', 'update_plan']);
      tools.forEach((tool, index) => {
        const cached = 'cache_control' in tool ? tool.cache_control : undefined;
        expect(cached).toEqual(index === tools.length - 1 ? { type: 'ephemeral' } : undefined);
        expect(tool).toMatchObject({ eager_input_streaming: true });
        expect(tool).not.toHaveProperty('strict');
        expect('input_schema' in tool ? tool.input_schema.type : undefined).toBe('object');
      });
      expect(body.cache_control).toEqual({ type: 'ephemeral' });

      // Server-side refusal fallback: everywhere except Haiku.
      if (model === 'claude-haiku-5-5') {
        expect(body).not.toHaveProperty('fallbacks');
        expect(body).not.toHaveProperty('betas');
        expect(supportsServerFallback(model)).toBe(false);
      } else {
        expect(body.fallbacks).toBe('default');
        expect(body.betas).toEqual(['server-side-fallback-2026-07-01']);
        expect(FALLBACK_BETA).toBe('server-side-fallback-2026-07-01');
        expect(supportsServerFallback(model)).toBe(true);
      }
    });
  }

  it('sends every effort level as given', () => {
    for (const effort of EFFORTS) {
      expect(buildAnthropicRequest(request('claude-sonnet-5-5', { effort })).output_config).toEqual({ effort });
    }
  });

  it('is deterministic and keeps the cached prefix free of volatile content', () => {
    const first = buildAnthropicRequest(request());
    const second = buildAnthropicRequest(request());
    expect(JSON.stringify(first)).toBe(JSON.stringify(second));
    const prefix = JSON.stringify({ tools: first.tools, system: first.system });
    // No dates, clock times or UUIDs in the tools + system prefix.
    expect(prefix).not.toMatch(/\b20\d\d-\d\d-\d\d\b/);
    expect(prefix).not.toMatch(/\b\d{1,2}:\d\d:\d\d\b/);
    expect(prefix).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i);
  });

  it('passes the messages through unchanged and never mutates the request', () => {
    const thinking = { type: 'thinking' as const, thinking: '', signature: 'sig' };
    const source = request('claude-opus-5-5', {
      messages: [
        { role: 'user', content: [{ type: 'text', text: 'Hi' }] },
        { role: 'assistant', content: [thinking, { type: 'text', text: 'Hello' }] },
        { role: 'user', content: [{ type: 'text', text: 'More' }] },
      ],
    });
    const snapshot = JSON.stringify(source);
    const body = buildAnthropicRequest(source);
    expect(body.messages).toEqual(source.messages);
    expect((body.messages[1]?.content as unknown[])[0]).toBe(thinking);
    expect(JSON.stringify(source)).toBe(snapshot);
    // No cache markers are written into the conversation itself.
    expect(JSON.stringify(body.messages)).not.toContain('cache_control');
  });

  it('marks the only tool when just the local tools are offered', () => {
    const body = buildAnthropicRequest(request('claude-opus-5-5', { tools: buildModelTools(FAKE_SOURCE, null) }));
    const tools = body.tools ?? [];
    expect(tools).toHaveLength(2);
    expect(tools[0]).not.toHaveProperty('cache_control');
    expect(tools[1]).toMatchObject({ name: 'update_plan', cache_control: { type: 'ephemeral' } });
  });
});

describe('createAnthropicTransport', () => {
  it('streams through client.beta.messages with the abort signal and reports text deltas per block', async () => {
    const stub = stubClient([
      {
        events: [
          { type: 'content_block_delta', index: 0, delta: { type: 'thinking_delta', thinking: 'hm' } } as BetaRawMessageStreamEvent,
          textDelta(1, 'Hel'),
          textDelta(1, 'lo.'),
          { type: 'content_block_delta', index: 2, delta: { type: 'input_json_delta', partial_json: '{"x1":' } },
        ],
        message: message({
          content: [
            { type: 'thinking', thinking: '', signature: 'sig' },
            { type: 'text', text: 'Hello.', citations: null },
            { type: 'tool_use', id: 'toolu_1', name: 'draw_line', input: { x1: 0 } },
          ] as BetaMessage['content'],
          stop_reason: 'tool_use',
        }),
      },
    ]);
    const transport = createAnthropicTransport({ client: stub.client });
    const controller = new AbortController();
    const h = hooks(controller.signal);
    const response = await transport.run(request(), h.value);

    expect(transport.kind).toBe('anthropic');
    expect(stub.bodies).toHaveLength(1);
    expect(stub.bodies[0]).toEqual(buildAnthropicRequest(request()));
    expect(stub.options[0]?.signal).toBe(controller.signal);
    // Only text is reported; thinking and tool-input fragments are not.
    expect(h.deltas).toEqual([
      { text: 'Hel', index: 1 },
      { text: 'lo.', index: 1 },
    ]);
    expect(response.stopReason).toBe('tool_use');
    expect(response.content.map((block) => block.type)).toEqual(['thinking', 'text', 'tool_use']);
    expect(response.model).toBe('claude-opus-5-5');
    expect(response.usage).toEqual({ inputTokens: 11, outputTokens: 5, cacheReadTokens: 900, cacheCreationTokens: 30 });
    expect(response.refusal).toBeUndefined();
  });

  it('reads stop_details only for a refusal', async () => {
    const stub = stubClient([
      {
        message: message({
          content: [],
          stop_reason: 'refusal',
          stop_details: { type: 'refusal', category: 'cyber', explanation: 'Declined.', fallback_credit_token: null } as BetaMessage['stop_details'],
        }),
      },
      { message: message({ content: [], stop_reason: 'refusal', stop_details: null }) },
      {
        message: message({
          stop_reason: 'end_turn',
          stop_details: { type: 'refusal', category: 'bio', explanation: 'stale' } as BetaMessage['stop_details'],
        }),
      },
    ]);
    const transport = createAnthropicTransport({ client: stub.client });
    const declined = await transport.run(request(), hooks().value);
    expect(declined.stopReason).toBe('refusal');
    expect(declined.refusal).toEqual({ category: 'cyber', explanation: 'Declined.' });
    const bare = await transport.run(request(), hooks().value);
    expect(bare.refusal).toEqual({ category: null, explanation: null });
    const normal = await transport.run(request(), hooks().value);
    expect(normal.refusal).toBeUndefined();
  });

  it('passes API errors through without retrying them', async () => {
    const error = new RateLimitError(429, {}, 'slow down', headers);
    const stub = stubClient([{ error }]);
    const transport = createAnthropicTransport({ client: stub.client });
    const h = hooks();
    await expect(transport.run(request(), h.value)).rejects.toBe(error);
    expect(stub.bodies).toHaveLength(1);
    expect(h.restarts()).toBe(0);

    const offline = new APIConnectionError({ message: 'fetch failed' });
    const second = stubClient([{ error: offline }]);
    await expect(createAnthropicTransport({ client: second.client }).run(request(), hooks().value)).rejects.toBe(offline);
    expect(second.bodies).toHaveLength(1);
  });

  it('asks again when a tool input could not be parsed, and tells the session to drop the partial text', async () => {
    const unreadable = new AnthropicError('Unable to parse tool parameter JSON from model.');
    const stub = stubClient([{ events: [textDelta(0, 'Draw')], error: unreadable }, { events: [textDelta(0, 'Drawing.')], message: message() }]);
    const transport = createAnthropicTransport({ client: stub.client });
    const h = hooks();
    const response = await transport.run(request(), h.value);
    expect(stub.bodies).toHaveLength(2);
    expect(stub.bodies[1]).toEqual(stub.bodies[0]);
    expect(h.restarts()).toBe(1);
    expect(stub.abortedStreams()).toBe(1);
    expect(response.stopReason).toBe('end_turn');
  });

  it('gives up after two re-asks', async () => {
    const unreadable = new AnthropicError('Unable to parse tool parameter JSON from model.');
    const stub = stubClient([{ error: unreadable }, { error: unreadable }, { error: unreadable }, { message: message() }]);
    const h = hooks();
    await expect(createAnthropicTransport({ client: stub.client }).run(request(), h.value)).rejects.toBe(unreadable);
    expect(stub.bodies).toHaveLength(3);
    expect(h.restarts()).toBe(2);
  });

  it('does not re-ask after the turn was aborted', async () => {
    const controller = new AbortController();
    controller.abort();
    const stub = stubClient([{ error: new AnthropicError('unreadable') }]);
    await expect(createAnthropicTransport({ client: stub.client }).run(request(), hooks(controller.signal).value)).rejects.toBeInstanceOf(AnthropicError);
    expect(stub.bodies).toHaveLength(1);
  });

  it('needs an API key or a client', () => {
    expect(() => createAnthropicTransport({})).toThrow(/API key or a client/);
    expect(() => createAnthropicTransport({ apiKey: '  ' })).toThrow(/API key or a client/);
    // Creating the real client does not touch the network.
    expect(createAnthropicTransport({ apiKey: 'sk-ant-test-key' }).kind).toBe('anthropic');
  });

  it('drives a whole session turn: request shape, streamed text, tool call, results', async () => {
    const stub = stubClient([
      {
        events: [textDelta(1, 'Drawing '), textDelta(1, 'the line.')],
        message: message({
          content: [
            { type: 'thinking', thinking: '', signature: 'sig-1' },
            { type: 'text', text: 'Drawing the line.', citations: null },
            { type: 'tool_use', id: 'toolu_a', name: 'draw_line', input: { x1: 0, y1: 0, x2: 5, y2: 0 } },
          ] as BetaMessage['content'],
          stop_reason: 'tool_use',
        }),
      },
      { events: [textDelta(0, 'Done.')], message: message({ content: [{ type: 'text', text: 'Done.', citations: null }], model: 'claude-fable-5-2' }) },
    ]);
    const cad = fakeExecutor();
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-fable-5-2',
      effort: 'medium',
      approvalMode: 'auto',
      executor: cad,
      transport: createAnthropicTransport({ client: stub.client }),
      toolSource: FAKE_SOURCE,
    });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Draw a line');

    expect(events.map((event) => event.type)).toEqual([
      'message.delta',
      'message.delta',
      'message.done',
      'tool.started',
      'tool.finished',
      'message.delta',
      'message.done',
      'turn.finished',
    ]);
    expect(events[2]).toMatchObject({ text: 'Drawing the line.' });
    expect(events.at(-1)).toMatchObject({
      stopReason: 'end_turn',
      model: 'claude-fable-5-2',
      usage: { inputTokens: 22, outputTokens: 10, cacheReadTokens: 1800, cacheCreationTokens: 60 },
    });

    const [first, second] = stub.bodies;
    expect(first?.model).toBe('claude-fable-5-2');
    expect(first?.output_config).toEqual({ effort: 'medium' });
    expect(first?.fallbacks).toBe('default');
    // The second request replays the assistant turn unchanged (thinking included) and answers the
    // tool call in one user message; tools and system are byte-identical to the first request.
    expect(second?.messages).toHaveLength(3);
    const assistant = second?.messages[1]?.content as { type: string; signature?: string }[];
    expect(assistant.map((block) => block.type)).toEqual(['thinking', 'text', 'tool_use']);
    expect(assistant[0]?.signature).toBe('sig-1');
    const results = second?.messages[2]?.content as { type: string; tool_use_id: string; is_error?: boolean }[];
    expect(results).toHaveLength(1);
    expect(results[0]).toMatchObject({ type: 'tool_result', tool_use_id: 'toolu_a' });
    expect(results[0]).not.toHaveProperty('is_error');
    expect(JSON.stringify(second?.tools)).toBe(JSON.stringify(first?.tools));
    expect(JSON.stringify(second?.system)).toBe(JSON.stringify(first?.system));
    expect(second?.messages[0]).toEqual(first?.messages[0]);
    expect(historyProblems(session.historySnapshot())).toEqual([]);
    expect(cad.calls).toHaveLength(1);
  });

  it('maps an SDK error thrown by the stream to one plain error event', async () => {
    const stub = stubClient([{ error: new AuthenticationError(401, {}, 'invalid x-api-key', headers) }]);
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-opus-5-5',
      effort: 'high',
      approvalMode: 'auto',
      executor: null,
      transport: createAnthropicTransport({ client: stub.client }),
    });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Hi');
    expect(events).toEqual([
      { type: 'error', message: 'The Anthropic API key is invalid or has been revoked. Update it in Settings.', retryable: false },
      { type: 'turn.finished', stopReason: 'error', usage: { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 }, model: 'claude-opus-5-5' },
    ]);
  });
});

describe('usageFromMessage', () => {
  it('reads the top-level usage', () => {
    expect(usageFromMessage(message().usage)).toEqual({ inputTokens: 11, outputTokens: 5, cacheReadTokens: 900, cacheCreationTokens: 30 });
  });

  it('treats missing counters as zero', () => {
    expect(usageFromMessage({ input_tokens: 3, output_tokens: 4, cache_read_input_tokens: null, cache_creation_input_tokens: null } as BetaMessage['usage'])).toEqual({
      inputTokens: 3,
      outputTokens: 4,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
    });
    expect(usageFromMessage(null)).toEqual({ inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 });
  });

  it('sums every attempt when a fallback model took over', () => {
    const usage = {
      input_tokens: 50,
      output_tokens: 20,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
      iterations: [
        { type: 'message', input_tokens: 40, output_tokens: 3, cache_read_input_tokens: 100, cache_creation_input_tokens: 0 },
        { type: 'fallback_message', input_tokens: 50, output_tokens: 20, cache_read_input_tokens: 0, cache_creation_input_tokens: 10 },
      ],
    } as unknown as BetaMessage['usage'];
    expect(usageFromMessage(usage)).toEqual({ inputTokens: 90, outputTokens: 23, cacheReadTokens: 100, cacheCreationTokens: 10 });
  });
});

describe('verifyApiKey', () => {
  it('accepts a key the Models API accepts, with one cheap call', async () => {
    const calls: unknown[] = [];
    const result = await verifyApiKey(' sk-ant-good ', {
      client: {
        models: {
          list: async (params) => {
            calls.push(params);
            return { data: [] };
          },
        },
      },
    });
    expect(result).toEqual({ ok: true });
    expect(calls).toEqual([{ limit: 1 }]);
  });

  it('rejects an empty key without calling anything', async () => {
    let called = false;
    const result = await verifyApiKey('   ', {
      client: {
        models: {
          list: async () => {
            called = true;
          },
        },
      },
    });
    expect(result).toEqual({ ok: false, message: 'Enter an Anthropic API key.' });
    expect(called).toBe(false);
  });

  it('maps failures to plain messages and never throws', async () => {
    const failing = (error: unknown) => ({
      client: {
        models: {
          list: async () => {
            throw error;
          },
        },
      },
    });
    expect(await verifyApiKey('sk-ant-bad', failing(new AuthenticationError(401, {}, 'invalid x-api-key', headers)))).toEqual({
      ok: false,
      message: 'The Anthropic API key is invalid or has been revoked. Update it in Settings.',
    });
    const offline = await verifyApiKey('sk-ant-x', failing(new APIConnectionError({ message: 'fetch failed' })));
    expect(offline).toEqual({ ok: false, message: 'Could not reach the Anthropic API. Check your internet connection and send again.' });
    const odd = await verifyApiKey('sk-ant-x', failing('boom'));
    expect(odd.ok).toBe(false);
  });
});
