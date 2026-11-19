// A scripted model for end-to-end tests and offline demos: it replays canned assistant turns and
// never touches the network. It is clearly not a real model (kind 'scripted', model 'scripted').
import { z } from 'zod';
import type { BetaContentBlock } from '@anthropic-ai/sdk/resources/beta/messages/messages';
import { formatValidationIssues } from '@cadstudio/cad-protocol';
import type { ModelRequest, ModelResponse, ModelRunHooks, ModelTransport } from './types';

/** Longest pause a scripted turn may request (milliseconds). */
export const MAX_SCRIPT_DELAY_MS = 60_000;
/** What the scripted model answers once every scripted turn has been used. */
export const SCRIPT_FINISHED_TEXT = 'Script finished.';
/** The model name the scripted transport reports. */
export const SCRIPTED_MODEL_NAME = 'scripted';

const scriptedTurnSchema = z.strictObject({
  /** Assistant text, streamed in a few deltas. */
  text: z.string().optional(),
  /** Tool calls the "model" makes after the text. */
  toolCalls: z
    .array(z.strictObject({ name: z.string().min(1), input: z.unknown().optional() }))
    .max(32)
    .optional(),
  /** Overrides the stop reason (default: 'tool_use' with tool calls, otherwise 'end_turn'). */
  stopReason: z.string().min(1).optional(),
  /** Pause before the turn completes, spread over the text deltas. */
  delayMs: z.number().min(0).max(MAX_SCRIPT_DELAY_MS).optional(),
});

const scriptSchema = z.strictObject({ turns: z.array(scriptedTurnSchema).max(1000) });

/** One canned assistant turn. */
export type ScriptedTurn = z.infer<typeof scriptedTurnSchema>;
/** A script for {@link createScriptedTransport}. */
export type ModelScript = z.infer<typeof scriptSchema>;

/**
 * Validates a script (an object, or its JSON text). Throws an Error that lists the problems when
 * it is malformed.
 */
export function parseModelScript(script: unknown): ModelScript {
  let value = script;
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value) as unknown;
    } catch (error) {
      throw new Error(`Invalid model script: not valid JSON (${error instanceof Error ? error.message : String(error)})`);
    }
  }
  const parsed = scriptSchema.safeParse(value);
  if (!parsed.success) {
    throw new Error(`Invalid model script:\n${formatValidationIssues(parsed.error.issues).map((line) => `- ${line}`).join('\n')}`);
  }
  return parsed.data;
}

function abortError(): Error {
  const error = new Error('The scripted model call was aborted.');
  error.name = 'AbortError';
  return error;
}

/** Waits `ms` milliseconds; rejects as soon as the signal aborts. */
function pause(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(abortError());
      return;
    }
    const onAbort = (): void => {
      clearTimeout(timer);
      reject(abortError());
    };
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    signal.addEventListener('abort', onAbort, { once: true });
  });
}

/** Splits text into at most `parts` chunks, breaking after whitespace where possible. */
function splitText(text: string, parts: number): string[] {
  if (text.length === 0) return [];
  const chunks: string[] = [];
  // Very short texts are not chopped into single characters.
  const target = Math.max(12, Math.ceil(text.length / parts));
  let start = 0;
  while (start < text.length && chunks.length < parts - 1) {
    let end = Math.min(text.length, start + target);
    const space = text.indexOf(' ', end);
    if (space !== -1 && space - end <= 12) end = space + 1;
    chunks.push(text.slice(start, end));
    start = end;
  }
  if (start < text.length) chunks.push(text.slice(start));
  return chunks;
}

/**
 * A scripted model for end-to-end tests and offline demos: replays the given assistant turns
 * (text and tool calls) in order, without any network access. Clearly not a real model.
 *
 * `script` is `{ turns: [{ text?, toolCalls?: [{ name, input }], stopReason?, delayMs? }] }` (or
 * its JSON text) and is validated; a malformed script throws. Every model request consumes the
 * next turn: the text is streamed in a few deltas (honouring `delayMs` and the abort signal), then
 * the tool calls follow as `tool_use` blocks with generated ids. After the last turn every request
 * is answered with 'Script finished.' and `end_turn`.
 */
export function createScriptedTransport(script: unknown): ModelTransport {
  const { turns } = parseModelScript(script);
  let nextTurn = 0;
  let nextToolId = 1;

  return {
    kind: 'scripted',
    async run(_request: ModelRequest, hooks: ModelRunHooks): Promise<ModelResponse> {
      if (hooks.signal.aborted) throw abortError();
      const turn: ScriptedTurn = turns[nextTurn] ?? { text: SCRIPT_FINISHED_TEXT, stopReason: 'end_turn' };
      nextTurn += 1;

      const text = turn.text ?? '';
      const chunks = splitText(text, 4);
      const delay = turn.delayMs ?? 0;
      const steps = Math.max(1, chunks.length);
      const stepDelay = delay / steps;

      if (chunks.length === 0) {
        if (delay > 0) await pause(delay, hooks.signal);
      } else {
        for (const chunk of chunks) {
          if (stepDelay > 0) await pause(stepDelay, hooks.signal);
          else await Promise.resolve();
          if (hooks.signal.aborted) throw abortError();
          hooks.onTextDelta(chunk, 0);
        }
      }
      if (hooks.signal.aborted) throw abortError();

      const content: BetaContentBlock[] = [];
      if (text.length > 0) content.push({ type: 'text', text, citations: null });
      const toolCalls = turn.toolCalls ?? [];
      for (const call of toolCalls) {
        content.push({
          type: 'tool_use',
          id: `toolu_scripted_${String(nextToolId).padStart(4, '0')}`,
          name: call.name,
          input: call.input ?? {},
        });
        nextToolId += 1;
      }

      return {
        content,
        stopReason: turn.stopReason ?? (toolCalls.length > 0 ? 'tool_use' : 'end_turn'),
        usage: { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        model: SCRIPTED_MODEL_NAME,
      };
    },
  };
}
