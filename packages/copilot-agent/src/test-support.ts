// Fakes shared by the unit tests of this package: a scripted-by-function model transport, a fake
// CAD executor with a small tool catalog of its own, and helpers to inspect events and history.
// Nothing here touches the network or a real CAD. Not exported from the package.
import { z } from 'zod';
import type {
  BetaContentBlock,
  BetaContentBlockParam,
  BetaMessageParam,
} from '@anthropic-ai/sdk/resources/beta/messages/messages';
import type { DocumentInfo, ToolDefinition, ToolResult } from '@cadstudio/cad-protocol';
import { CopilotSession } from './session';
import type { SessionOptions } from './session';
import { createToolSource } from './tools';
import type { ToolSource } from './tools';
import type {
  CadToolExecutor,
  ModelRequest,
  ModelResponse,
  ModelRunHooks,
  ModelTransport,
  SessionEvent,
  TurnUsage,
} from './types';

// ---------------------------------------------------------------------------------------------
// Tool definitions
// ---------------------------------------------------------------------------------------------

function define(
  name: string,
  title: string,
  risk: ToolDefinition['risk'],
  input: z.ZodType,
): ToolDefinition {
  return { name, title, description: `${title} (test tool).`, category: 'geometry', risk, input, output: z.unknown() };
}

/** A small catalog: one read tool, two write tools, one destructive tool. */
export const FAKE_TOOLS: readonly ToolDefinition[] = [
  define('get_info', 'Get info', 'read', z.strictObject({})),
  define(
    'draw_line',
    'Draw line',
    'write',
    z.strictObject({
      x1: z.number(),
      y1: z.number(),
      x2: z.number(),
      y2: z.number(),
      layer: z.string().min(1).default('0'),
    }),
  ),
  define('draw_arc', 'Draw arc', 'write', z.strictObject({ radius: z.number().positive() })),
  define('delete_things', 'Delete things', 'destructive', z.strictObject({ ids: z.array(z.string()).min(1) })),
];

/** The fake catalog as a tool source. */
export const FAKE_SOURCE: ToolSource = createToolSource(FAKE_TOOLS);

/** Names the default fake CAD supports (draw_arc is in the catalog but not supported). */
export const FAKE_SUPPORTED: readonly string[] = ['delete_things', 'draw_line', 'get_info'];

// ---------------------------------------------------------------------------------------------
// Model transport
// ---------------------------------------------------------------------------------------------

export const ZERO_USAGE: TurnUsage = { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 };

export interface ReplySpec {
  /** Thinking block placed first (signature generated). */
  thinking?: boolean;
  text?: string;
  tools?: { id?: string; name: string; input?: unknown }[];
  stopReason?: string | null;
  usage?: Partial<TurnUsage>;
  model?: string;
  refusal?: { category: string | null; explanation: string | null };
}

let replyCounter = 0;

/** Builds an assistant response: optional thinking, optional text, then tool calls. */
export function reply(spec: ReplySpec = {}): ModelResponse {
  const content: BetaContentBlock[] = [];
  replyCounter += 1;
  if (spec.thinking === true) {
    content.push({ type: 'thinking', thinking: '', signature: `sig-${replyCounter}` });
  }
  if (spec.text !== undefined) content.push({ type: 'text', text: spec.text, citations: null });
  (spec.tools ?? []).forEach((tool, index) => {
    content.push({
      type: 'tool_use',
      id: tool.id ?? `toolu_${replyCounter}_${index}`,
      name: tool.name,
      input: tool.input ?? {},
    });
  });
  const response: ModelResponse = {
    content,
    stopReason: spec.stopReason === undefined ? ((spec.tools ?? []).length > 0 ? 'tool_use' : 'end_turn') : spec.stopReason,
    usage: { ...ZERO_USAGE, inputTokens: 10, outputTokens: 5, ...spec.usage },
    model: spec.model ?? 'fake-model',
  };
  if (spec.refusal !== undefined) response.refusal = spec.refusal;
  return response;
}

/** One scripted step: a response, or a function that produces (or fails to produce) one. */
export type Step = ModelResponse | ((request: ModelRequest, hooks: ModelRunHooks) => Promise<ModelResponse> | ModelResponse);

export interface FakeTransport extends ModelTransport {
  /** Every request received, in order (messages are the snapshot the session passed). */
  readonly requests: ModelRequest[];
}

/**
 * A transport that answers the n-th request with the n-th step. Response text is streamed in two
 * deltas first. When the steps run out it answers 'Done.' with end_turn.
 */
export function fakeTransport(steps: readonly Step[]): FakeTransport {
  const requests: ModelRequest[] = [];
  return {
    kind: 'fake',
    requests,
    async run(request, hooks) {
      requests.push(request);
      const step = steps[requests.length - 1] ?? reply({ text: 'Done.' });
      const response = typeof step === 'function' ? await step(request, hooks) : step;
      response.content.forEach((block, index) => {
        if (block.type !== 'text' || block.text.length === 0) return;
        const half = Math.ceil(block.text.length / 2);
        hooks.onTextDelta(block.text.slice(0, half), index);
        if (half < block.text.length) hooks.onTextDelta(block.text.slice(half), index);
      });
      return response;
    },
  };
}

/** A step that never answers until the turn is aborted, then rejects like an aborted request. */
export function hangUntilAborted(onStarted?: (hooks: ModelRunHooks) => void): Step {
  return (_request, hooks) =>
    new Promise<ModelResponse>((_resolve, reject) => {
      onStarted?.(hooks);
      const abort = (): void => {
        const error = new Error('aborted');
        error.name = 'AbortError';
        reject(error);
      };
      if (hooks.signal.aborted) abort();
      else hooks.signal.addEventListener('abort', abort, { once: true });
    });
}

// ---------------------------------------------------------------------------------------------
// CAD executor
// ---------------------------------------------------------------------------------------------

export interface ExecutorCall {
  name: string;
  args: unknown;
  opts: { operationId: string; instruction: string; signal: AbortSignal; expectedRevision?: number };
}

export type ToolHandler = (call: ExecutorCall, cad: FakeExecutor) => Promise<ToolResult> | ToolResult;

export interface FakeExecutor extends CadToolExecutor {
  readonly calls: ExecutorCall[];
  document: DocumentInfo | null;
  name: string;
  supported: string[];
  handlers: Record<string, ToolHandler>;
  /** Largest number of execute() calls that were running at the same time. */
  maxConcurrent: number;
}

/** A successful ToolResult at the given revision. */
export function okResult(revision: number, extra: Partial<ToolResult> = {}): ToolResult {
  return {
    success: true,
    createdIds: [],
    modifiedIds: [],
    deletedIds: [],
    warnings: [],
    errors: [],
    revision,
    ...extra,
  };
}

/**
 * A fake CAD: read tools succeed without changing the revision, other tools bump it by one and
 * report a created id. `handlers[name]` overrides the behaviour of one tool.
 */
export function fakeExecutor(init: Partial<Pick<FakeExecutor, 'document' | 'name' | 'supported' | 'handlers'>> = {}): FakeExecutor {
  let running = 0;
  const cad: FakeExecutor = {
    calls: [],
    document:
      init.document === undefined
        ? { id: 'doc-1', name: 'Plan.cadstudio', path: null, unit: 'mm', revision: 7, entityCount: 42 }
        : init.document,
    name: init.name ?? 'CAD Studio',
    supported: init.supported ?? [...FAKE_SUPPORTED],
    handlers: init.handlers ?? {},
    maxConcurrent: 0,
    cadName: () => cad.name,
    supportedTools: () => cad.supported,
    currentDocument: () => cad.document,
    async execute(name, args, opts) {
      const call: ExecutorCall = { name, args, opts };
      cad.calls.push(call);
      running += 1;
      cad.maxConcurrent = Math.max(cad.maxConcurrent, running);
      try {
        const handler = cad.handlers[name];
        if (handler !== undefined) return await handler(call, cad);
        await Promise.resolve();
        const revision = cad.document?.revision ?? 0;
        if (FAKE_TOOLS.find((tool) => tool.name === name)?.risk === 'read') {
          return okResult(revision, { data: { entityCount: cad.document?.entityCount ?? 0 } });
        }
        if (cad.document !== null) cad.document = { ...cad.document, revision: revision + 1 };
        return okResult(revision + 1, { createdIds: [`e${cad.calls.length}`] });
      } finally {
        running -= 1;
      }
    },
  };
  return cad;
}

// ---------------------------------------------------------------------------------------------
// Session harness
// ---------------------------------------------------------------------------------------------

export interface Harness {
  session: CopilotSession;
  events: SessionEvent[];
  transport: FakeTransport;
  cad: FakeExecutor | null;
  logs: { message: string; error: unknown }[];
  /** Event types in order. */
  types(): string[];
  /** Events of one type. */
  of<T extends SessionEvent['type']>(type: T): Extract<SessionEvent, { type: T }>[];
  /** Resolves with the next event of the given type (register before the event can happen). */
  next<T extends SessionEvent['type']>(type: T): Promise<Extract<SessionEvent, { type: T }>>;
}

/** A session wired to a fake transport and (unless `executor: null`) a fake CAD. */
export function harness(
  steps: readonly Step[],
  options: Partial<SessionOptions> & { cad?: FakeExecutor | null } = {},
): Harness {
  const transport = fakeTransport(steps);
  const cad = options.cad === undefined ? fakeExecutor() : options.cad;
  const logs: { message: string; error: unknown }[] = [];
  const { cad: _cad, ...sessionOptions } = options;
  const session = new CopilotSession({
    apiKey: null,
    model: 'claude-opus-5-5',
    effort: 'high',
    approvalMode: 'ask-destructive',
    executor: cad,
    transport,
    toolSource: FAKE_SOURCE,
    logger: (message, error) => {
      logs.push({ message, error });
    },
    ...sessionOptions,
  });
  const events: SessionEvent[] = [];
  const waiters: { type: string; resolve: (event: SessionEvent) => void }[] = [];
  session.on((event) => {
    events.push(event);
    for (let index = 0; index < waiters.length; index += 1) {
      const waiter = waiters[index];
      if (waiter !== undefined && waiter.type === event.type) {
        waiters.splice(index, 1);
        waiter.resolve(event);
        break;
      }
    }
  });
  return {
    session,
    events,
    transport,
    cad,
    logs,
    types: () => events.map((event) => event.type),
    of: <T extends SessionEvent['type']>(type: T) =>
      events.filter((event): event is Extract<SessionEvent, { type: T }> => event.type === type),
    next: <T extends SessionEvent['type']>(type: T) =>
      new Promise<Extract<SessionEvent, { type: T }>>((resolve) => {
        waiters.push({ type, resolve: (event) => resolve(event as Extract<SessionEvent, { type: T }>) });
      }),
  };
}

// ---------------------------------------------------------------------------------------------
// History inspection
// ---------------------------------------------------------------------------------------------

type Block = BetaContentBlockParam;

/** The content blocks of a message (a string content counts as one text block). */
export function blocksOf(message: BetaMessageParam | undefined): Block[] {
  if (message === undefined || message.content === undefined) return [];
  return typeof message.content === 'string' ? [{ type: 'text', text: message.content }] : message.content;
}

/** Texts of the text blocks of a message. */
export function textsOf(message: BetaMessageParam | undefined): string[] {
  return blocksOf(message).flatMap((block) => (block.type === 'text' ? [block.text] : []));
}

/** The tool_result blocks of a message. */
export function toolResultsOf(message: BetaMessageParam | undefined): Extract<Block, { type: 'tool_result' }>[] {
  return blocksOf(message).filter((block): block is Extract<Block, { type: 'tool_result' }> => block.type === 'tool_result');
}

/**
 * Problems that would make the API reject the history: it must start with a user message, every
 * tool call must be answered by a tool result in the next message (same order, nothing else
 * before them), and no tool result may be orphaned. An empty list means the history is valid.
 */
export function historyProblems(history: readonly BetaMessageParam[]): string[] {
  const problems: string[] = [];
  if (history.length > 0 && history[0]?.role !== 'user') problems.push('the first message is not a user message');
  history.forEach((message, index) => {
    const blocks = blocksOf(message);
    if (blocks.length === 0) problems.push(`message ${index} is empty`);
    if (message.role === 'assistant') {
      const uses = blocks.flatMap((block) => (block.type === 'tool_use' ? [block.id] : []));
      if (uses.length === 0) return;
      const next = history[index + 1];
      const results = next !== undefined && next.role === 'user' ? toolResultsOf(next).map((block) => block.tool_use_id) : [];
      if (JSON.stringify(results) !== JSON.stringify(uses)) {
        problems.push(`message ${index}: tool calls ${uses.join(',')} are answered by [${results.join(',')}]`);
      }
      const leading = next === undefined ? [] : blocksOf(next).slice(0, uses.length);
      if (leading.some((block) => block.type !== 'tool_result')) {
        problems.push(`message ${index + 1}: tool results must come first`);
      }
    } else if (message.role === 'user') {
      const results = toolResultsOf(message);
      if (results.length === 0) return;
      const previous = history[index - 1];
      const uses =
        previous !== undefined && previous.role === 'assistant'
          ? blocksOf(previous).flatMap((block) => (block.type === 'tool_use' ? [block.id] : []))
          : [];
      for (const result of results) {
        if (!uses.includes(result.tool_use_id)) problems.push(`message ${index}: orphan tool result ${result.tool_use_id}`);
      }
    }
  });
  return problems;
}

/** The `tool_result` text for one tool call id, searched in the whole history. */
export function resultTextFor(history: readonly BetaMessageParam[], toolUseId: string): string | undefined {
  for (const message of history) {
    for (const block of toolResultsOf(message)) {
      if (block.tool_use_id === toolUseId) return typeof block.content === 'string' ? block.content : JSON.stringify(block.content);
    }
  }
  return undefined;
}

/** Whether the `tool_result` for a tool call id is flagged as an error. */
export function resultIsError(history: readonly BetaMessageParam[], toolUseId: string): boolean | undefined {
  for (const message of history) {
    for (const block of toolResultsOf(message)) {
      if (block.tool_use_id === toolUseId) return block.is_error === true;
    }
  }
  return undefined;
}
