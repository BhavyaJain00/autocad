// CopilotSession: the agent loop of CAD Copilot. It owns the conversation history, runs the model,
// executes the model's tool calls against the connected CAD (with validation, approvals and stale
// revision protection), answers the two local tools itself and reports everything as events.
import { randomUUID } from 'node:crypto';
import type {
  BetaContentBlock,
  BetaContentBlockParam,
  BetaMessageParam,
  BetaToolResultBlockParam,
  BetaToolUseBlockParam,
} from '@anthropic-ai/sdk/resources/beta/messages/messages';
import { toolResultSchema } from '@cadstudio/cad-protocol';
import type { DocumentInfo, ToolDefinition, ToolResult } from '@cadstudio/cad-protocol';
import { mapAttachments } from './attachments';
import { describeModelError } from './errors';
import { SYSTEM_PROMPT, buildContextBlock } from './prompt';
import type { TurnContext } from './prompt';
import {
  CANCELLED_MESSAGE,
  CANCELLED_RUNNING_MESSAGE,
  DECLINED_MESSAGE,
  errorResultText,
  serializeToolResult,
} from './tool-result';
import {
  ASK_USER_TOOL,
  CATALOG_TOOL_SOURCE,
  UPDATE_PLAN_TOOL,
  buildModelTools,
  isLocalTool,
  parseAskUser,
  parseUpdatePlan,
} from './tools';
import type { ToolSource } from './tools';
import { createAnthropicTransport } from './transport-anthropic';
import { SCRIPTED_MODEL_NAME } from './transport-scripted';
import { DEFAULT_APPROVAL_MODE, DEFAULT_EFFORT, DEFAULT_MODEL, EFFORTS, MODEL_IDS } from './types';
import type {
  ApprovalMode,
  Attachment,
  CadToolExecutor,
  Effort,
  ModelId,
  ModelRefusal,
  ModelResponse,
  ModelToolSpec,
  ModelTransport,
  Question,
  QuestionAnswer,
  SessionEvent,
  TurnUsage,
} from './types';

/** Most model calls in one user turn before the session stops the loop. */
export const DEFAULT_MAX_MODEL_CALLS = 80;

const APPROVAL_MODES: readonly ApprovalMode[] = ['ask-all', 'ask-destructive', 'auto'];

const MESSAGES = {
  busy: 'CAD Copilot is still working on the previous message. Wait for it to finish, or stop it first.',
  empty: 'Type a message or attach a file first.',
  noApiKey: 'Add your Anthropic API key in Settings to start using CAD Copilot.',
  maxTokens:
    'The model reached its output limit before it finished, so its last step is incomplete. Send "continue" to let it carry on.',
  contextFull: 'This conversation has become too long for the model. Start a new conversation to continue.',
  notRunCutOff: 'Not run: the reply was cut off at the output limit before this tool call was complete. Call it again.',
} as const;

/** A user's decision on an approval card. */
export type ApprovalDecision = 'approve' | 'reject' | 'approve-all';

export interface SessionOptions {
  /** Anthropic API key. When null the session reports a clear error on send(). */
  apiKey: string | null;
  model: ModelId;
  effort: Effort;
  approvalMode: ApprovalMode;
  /** The connected CAD, or null when no CAD is connected (CAD tools are then unavailable). */
  executor: CadToolExecutor | null;
  /** Replaces the Anthropic client (tests / scripted demo model). */
  transport?: ModelTransport;
  /** Where CAD tool definitions come from. Default: the shared catalog of @cadstudio/cad-protocol. */
  toolSource?: ToolSource;
  /** Most model calls in one user turn (default 80). */
  maxModelCalls?: number;
  /** Receives internal problems that must not break the session (default: console.error). */
  logger?: (message: string, error?: unknown) => void;
}

/** True when a tool of this risk needs the user's approval under the given mode. */
export function requiresApproval(risk: ToolDefinition['risk'], mode: ApprovalMode): boolean {
  if (risk === 'destructive') return true;
  if (risk === 'write') return mode === 'ask-all';
  return false;
}

/**
 * The assistant content to put into the history for a response.
 *
 * Normally that is the content exactly as the model produced it, the same block objects in the
 * same order, thinking blocks included. Two things the API does not accept back are left out:
 *
 * - text blocks without any text (an empty text block is rejected as input);
 * - after a fallback model took over from a model that declined midway: the blocks of the declined
 *   attempt (everything before the last `fallback` block) other than its text and the `fallback`
 *   markers, i.e. thinking, redacted thinking, tool calls and other model-internal blocks. Tool
 *   calls that are left out are never executed and get no tool result.
 */
export function echoableContent(content: readonly BetaContentBlock[]): BetaContentBlockParam[] {
  let boundary = -1;
  content.forEach((block, index) => {
    if (block.type === 'fallback') boundary = index;
  });
  const kept = content.filter((block, index) => {
    if (block.type === 'text') return block.text.length > 0;
    return index >= boundary || block.type === 'fallback';
  });
  // Response blocks are accepted back as request blocks as they are.
  return kept as BetaContentBlockParam[];
}

/**
 * A copy of the history without any thinking blocks. Used once, at the start of a turn, when the
 * tool list offered to the model has changed (a CAD was connected, disconnected or swapped):
 * thinking blocks are bound to the tool list they were produced with and would be rejected, while
 * a history without any of them stays valid. Text, tool calls and tool results are kept.
 */
export function withoutThinking(history: readonly BetaMessageParam[]): BetaMessageParam[] {
  const out: BetaMessageParam[] = [];
  for (const message of history) {
    if (message.role !== 'assistant' || typeof message.content === 'string') {
      out.push(message);
      continue;
    }
    const kept = message.content.filter((block) => block.type !== 'thinking' && block.type !== 'redacted_thinking');
    if (kept.length === message.content.length) out.push(message);
    else if (kept.length > 0) out.push({ ...message, content: kept });
    // A message that held nothing but reasoning carries no information any more and is dropped.
  }
  return out;
}

function isToolUse(block: BetaContentBlockParam): block is BetaToolUseBlockParam {
  return block.type === 'tool_use';
}

function emptyUsage(): TurnUsage {
  return { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 };
}

function newId(prefix: string): string {
  return `${prefix}_${randomUUID()}`;
}

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim().length > 0) return error.message.trim();
  if (typeof error === 'string' && error.trim().length > 0) return error.trim();
  return 'unknown error';
}

function abortError(): Error {
  const error = new Error('Aborted');
  error.name = 'AbortError';
  return error;
}

/**
 * Settles with `work()`, or rejects as soon as the signal aborts (whichever comes first), so that
 * stopping is immediate even when the underlying call is slow to notice the signal. The abandoned
 * call's late outcome is ignored.
 */
function raceAbort<T>(work: () => Promise<T>, signal: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    if (signal.aborted) {
      reject(abortError());
      return;
    }
    const onAbort = (): void => reject(abortError());
    signal.addEventListener('abort', onAbort, { once: true });
    let promise: Promise<T>;
    try {
      promise = Promise.resolve(work());
    } catch (error) {
      signal.removeEventListener('abort', onAbort);
      reject(error);
      return;
    }
    promise.then(
      (value) => {
        signal.removeEventListener('abort', onAbort);
        resolve(value);
      },
      (error: unknown) => {
        signal.removeEventListener('abort', onAbort);
        reject(error);
      },
    );
  });
}

const REFUSAL_REASONS: Readonly<Record<string, string>> = {
  cyber: 'its cybersecurity safeguards were triggered',
  bio: 'its biology safeguards were triggered',
  frontier_llm: 'it is restricted from helping with AI model development',
  reasoning_extraction: 'it was asked to reveal its internal reasoning',
  general_harms: 'its usage-policy safeguards were triggered',
};

function refusalMessage(refusal: ModelRefusal | null | undefined): string {
  const reason = refusal?.category ? REFUSAL_REASONS[refusal.category] : undefined;
  const explanation = refusal?.explanation?.replace(/\s+/g, ' ').trim();
  let message = reason === undefined ? 'The model declined this request.' : `The model declined this request: ${reason}.`;
  if (explanation !== undefined && explanation.length > 0) {
    message += ` ${explanation.length > 300 ? `${explanation.slice(0, 297)}...` : explanation}`;
  }
  return `${message} Try rephrasing it, or choose another model in Settings.`;
}

/** The text of a tool result and whether it reports a failure. */
interface ToolOutcome {
  text: string;
  isError: boolean;
}

const failure = (text: string): ToolOutcome => ({ text, isError: true });

interface PendingQuestion {
  requestId: string;
  questions: Question[];
  resolve(answers: QuestionAnswer[] | null): void;
}

interface PendingApproval {
  requestId: string;
  resolve(decision: ApprovalDecision | null): void;
}

/** State of the running user turn. */
interface ActiveTurn {
  readonly controller: AbortController;
  /** The history array this turn appends to (reset() swaps the session's array, not this one). */
  history: BetaMessageParam[];
  usage: TurnUsage;
  model: string;
  stopping: boolean;
  pendingQuestion: PendingQuestion | null;
  pendingApproval: PendingApproval | null;
  /** Resolves when the turn has completely finished. */
  readonly done: Promise<void>;
  finish(): void;
}

/** Everything a turn fixes at its start (settings changed meanwhile apply to the next turn). */
interface TurnSetup {
  transport: ModelTransport;
  model: ModelId;
  effort: Effort;
  executor: CadToolExecutor | null;
  cadName: string | null;
  supported: ReadonlySet<string>;
  tools: ModelToolSpec[];
  instruction: string;
}

/** Turns streamed text deltas of one model call into message.delta / message.done events. */
class TextStream {
  private readonly messages: { index: number; id: string; text: string; done: boolean }[] = [];

  constructor(private readonly emit: (event: SessionEvent) => void) {}

  delta(text: string, blockIndex: number): void {
    if (text.length === 0) return;
    let current = this.messages[this.messages.length - 1];
    if (current === undefined || current.done || current.index !== blockIndex) {
      if (current !== undefined) this.close(current);
      current = { index: blockIndex, id: newId('msg'), text: '', done: false };
      this.messages.push(current);
    }
    current.text += text;
    this.emit({ type: 'message.delta', messageId: current.id, text });
  }

  /** Closes every open message with the text streamed so far (failure, stop or a restart). */
  closeAll(): void {
    for (const message of this.messages) this.close(message);
  }

  /** Forgets the streamed messages after closing them: the transport is asking the model again. */
  restart(): void {
    this.closeAll();
    this.messages.length = 0;
  }

  /**
   * Completes the messages with the final content: the n-th streamed message is the n-th text
   * block. Text blocks that were not streamed (a transport without streaming) are emitted whole.
   */
  finish(content: readonly BetaContentBlock[]): void {
    let position = 0;
    for (const block of content) {
      if (block.type !== 'text' || block.text.length === 0) continue;
      const streamed = this.messages[position];
      position += 1;
      if (streamed === undefined) {
        const id = newId('msg');
        this.emit({ type: 'message.delta', messageId: id, text: block.text });
        this.emit({ type: 'message.done', messageId: id, text: block.text });
      } else if (!streamed.done) {
        streamed.text = block.text;
        this.close(streamed);
      }
    }
    this.closeAll();
  }

  private close(message: { id: string; text: string; done: boolean }): void {
    if (message.done) return;
    message.done = true;
    this.emit({ type: 'message.done', messageId: message.id, text: message.text });
  }
}

/**
 * One conversation with CAD Copilot.
 *
 * `send()` runs a user turn: the model is called, its tool calls are executed one after another
 * against the connected CAD, the results go back to the model, and so on until the model has
 * finished. Progress is reported through events (`on()`), questions and approvals are answered with
 * `answerQuestion()` / `resolveApproval()`, and `stop()` cancels the turn at any point. Every
 * accepted `send()` ends with exactly one `turn.finished` event.
 *
 * The message history is append-only and always valid for the API: every recorded tool call has a
 * tool result, also after a stop or an error, so the user can simply send again.
 */
export class CopilotSession {
  private readonly listeners = new Set<(event: SessionEvent) => void>();
  private readonly customTransport: ModelTransport | undefined;
  private readonly toolSource: ToolSource;
  private readonly maxModelCalls: number;
  private readonly log: (message: string, error?: unknown) => void;

  private apiKey: string | null;
  private anthropicTransport: ModelTransport | null = null;
  private modelId: ModelId;
  private effortLevel: Effort;
  private approval: ApprovalMode;
  private executor: CadToolExecutor | null;

  private history: BetaMessageParam[] = [];
  /** The tool list the history was produced with (thinking blocks are bound to it). */
  private toolFingerprint: string | null = null;
  /** The drawing revision the model last saw, and the drawing it belongs to. */
  private seenRevision: number | undefined;
  private seenDocumentId: string | undefined;
  private turn: ActiveTurn | null = null;

  constructor(options: SessionOptions) {
    this.apiKey = normaliseKey(options.apiKey);
    this.modelId = MODEL_IDS.includes(options.model) ? options.model : DEFAULT_MODEL;
    this.effortLevel = EFFORTS.includes(options.effort) ? options.effort : DEFAULT_EFFORT;
    this.approval = APPROVAL_MODES.includes(options.approvalMode) ? options.approvalMode : DEFAULT_APPROVAL_MODE;
    this.executor = options.executor ?? null;
    this.customTransport = options.transport;
    this.toolSource = options.toolSource ?? CATALOG_TOOL_SOURCE;
    const maxCalls = options.maxModelCalls;
    this.maxModelCalls =
      typeof maxCalls === 'number' && Number.isInteger(maxCalls) && maxCalls > 0 ? maxCalls : DEFAULT_MAX_MODEL_CALLS;
    this.log =
      options.logger ??
      ((message, error) => {
        console.error(`[copilot-agent] ${message}`, error ?? '');
      });
  }

  /** Subscribe to events; returns unsubscribe. Listener errors are caught and logged. */
  on(listener: (e: SessionEvent) => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /**
   * Starts a user turn and resolves when it has finished (normally, by stop(), or with an error
   * event). Never rejects. Calling it while busy emits an error event and resolves immediately.
   */
  async send(text: string, attachments?: Attachment[]): Promise<void> {
    // A turn that is being stopped finishes within a few ticks; wait for it instead of refusing.
    if (this.turn !== null && this.turn.stopping) await this.turn.done;
    if (this.turn !== null) {
      this.emit({ type: 'error', message: MESSAGES.busy, retryable: true });
      return;
    }

    let finish: () => void = () => undefined;
    const done = new Promise<void>((resolve) => {
      finish = resolve;
    });
    const turn: ActiveTurn = {
      controller: new AbortController(),
      history: this.history,
      usage: emptyUsage(),
      model: this.modelId,
      stopping: false,
      pendingQuestion: null,
      pendingApproval: null,
      done,
      finish,
    };
    this.turn = turn;

    let stopReason: string;
    try {
      stopReason = await this.runTurn(turn, text, attachments ?? []);
    } catch (error) {
      // Not reachable through model, tool or listener failures (all handled); a safety net so that
      // send() never rejects and the session never stays busy.
      this.log('The turn failed unexpectedly', error);
      if (turn.controller.signal.aborted) {
        stopReason = 'stopped';
      } else {
        this.emit({
          type: 'error',
          message: `CAD Copilot ran into an internal problem: ${errorMessage(error)}. Send again to retry.`,
          retryable: true,
        });
        stopReason = 'error';
      }
    }
    if (this.turn === turn) this.turn = null;
    turn.finish();
    this.emit({ type: 'turn.finished', stopReason, usage: { ...turn.usage }, model: turn.model });
  }

  /** Answers a `question.asked` request. Unknown or outdated request ids are ignored. */
  answerQuestion(requestId: string, answers: QuestionAnswer[]): void {
    const pending = this.turn?.pendingQuestion;
    if (pending === undefined || pending === null || pending.requestId !== requestId) return;
    const known = new Set(pending.questions.map((question) => question.id));
    const clean: QuestionAnswer[] = [];
    if (Array.isArray(answers)) {
      for (const entry of answers as unknown[]) {
        if (typeof entry !== 'object' || entry === null) continue;
        const { questionId, answer } = entry as { questionId?: unknown; answer?: unknown };
        if (typeof questionId !== 'string' || typeof answer !== 'string') continue;
        if (!known.has(questionId) || answer.trim().length === 0) continue;
        known.delete(questionId);
        clean.push({ questionId, answer: answer.trim() });
      }
    }
    pending.resolve(clean);
  }

  /** Resolves an `approval.requested` request. Unknown or outdated request ids are ignored. */
  resolveApproval(requestId: string, decision: ApprovalDecision): void {
    const pending = this.turn?.pendingApproval;
    if (pending === undefined || pending === null || pending.requestId !== requestId) return;
    if (decision !== 'approve' && decision !== 'reject' && decision !== 'approve-all') return;
    pending.resolve(decision);
  }

  /** Aborts the running turn (model stream, pending question/approval, running tool call). */
  stop(): void {
    const turn = this.turn;
    if (turn === null || turn.stopping) return;
    turn.stopping = true;
    turn.controller.abort();
    turn.pendingQuestion?.resolve(null);
    turn.pendingApproval?.resolve(null);
  }

  /** Sets or removes the API key. Takes effect on the next model call that starts a turn. */
  setApiKey(apiKey: string | null): void {
    const key = normaliseKey(apiKey);
    if (key === this.apiKey) return;
    this.apiKey = key;
    this.anthropicTransport = null;
  }

  /** Switches the model. Takes effect on the next turn. */
  setModel(model: ModelId): void {
    if (MODEL_IDS.includes(model)) this.modelId = model;
  }

  /** Switches the effort level. Takes effect on the next turn. */
  setEffort(effort: Effort): void {
    if (EFFORTS.includes(effort)) this.effortLevel = effort;
  }

  /** Switches the approval mode. Takes effect on the next tool call. */
  setApprovalMode(mode: ApprovalMode): void {
    if (APPROVAL_MODES.includes(mode)) this.approval = mode;
  }

  /** Switches the connected CAD (or null). Takes effect on the next turn. */
  setExecutor(executor: CadToolExecutor | null): void {
    const next = executor ?? null;
    if (next === this.executor) return;
    this.executor = next;
    // Revisions of another CAD connection mean nothing for this one.
    this.seenRevision = undefined;
    this.seenDocumentId = undefined;
  }

  /** Stops any running turn and clears the conversation. */
  reset(): void {
    this.stop();
    // A turn that is still unwinding keeps appending to its own (now detached) history array.
    this.history = [];
    this.toolFingerprint = null;
    this.seenRevision = undefined;
    this.seenDocumentId = undefined;
  }

  /** True while a turn is running. */
  get busy(): boolean {
    return this.turn !== null;
  }

  /** The model used for the next turn. */
  get model(): ModelId {
    return this.modelId;
  }

  /** The effort level used for the next turn. */
  get effort(): Effort {
    return this.effortLevel;
  }

  /** The current approval mode ('approve-all' on an approval card switches it to 'auto'). */
  get approvalMode(): ApprovalMode {
    return this.approval;
  }

  /** A copy of the conversation history as it is sent to the model (for diagnostics and tests). */
  historySnapshot(): BetaMessageParam[] {
    return [...this.history];
  }

  // -------------------------------------------------------------------------------------------
  // Turn
  // -------------------------------------------------------------------------------------------

  private emit(event: SessionEvent): void {
    for (const listener of [...this.listeners]) {
      try {
        listener(event);
      } catch (error) {
        this.log(`An event listener failed on "${event.type}"`, error);
      }
    }
  }

  private fail(message: string, retryable: boolean): void {
    this.emit({ type: 'error', message, retryable });
  }

  private resolveTransport(): ModelTransport | null {
    if (this.customTransport !== undefined) return this.customTransport;
    if (this.apiKey === null) return null;
    this.anthropicTransport ??= createAnthropicTransport({ apiKey: this.apiKey });
    return this.anthropicTransport;
  }

  /** Runs one user turn and returns its stop reason. Emits everything except `turn.finished`. */
  private async runTurn(turn: ActiveTurn, text: string, attachments: readonly Attachment[]): Promise<string> {
    const instruction = typeof text === 'string' ? text.trim() : '';
    const files = Array.isArray(attachments) ? attachments : [];
    if (instruction.length === 0 && files.length === 0) {
      this.fail(MESSAGES.empty, false);
      return 'error';
    }
    const mapping = mapAttachments(files);
    if (!mapping.ok) {
      this.fail(mapping.message, false);
      return 'error';
    }
    const transport = this.resolveTransport();
    if (transport === null) {
      this.fail(MESSAGES.noApiKey, false);
      return 'error';
    }

    // Everything the turn needs from the CAD and the settings is read once, here.
    const executor = this.executor;
    let cadName: string | null = null;
    let supportedNames: string[] | null = null;
    let document: DocumentInfo | null = null;
    if (executor !== null) {
      try {
        cadName = executor.cadName();
        supportedNames = [...executor.supportedTools()];
        document = executor.currentDocument();
      } catch (error) {
        this.fail(`The CAD connection is not responding: ${errorMessage(error)}. Reconnect and send again.`, true);
        return 'error';
      }
    }
    const tools = buildModelTools(this.toolSource, supportedNames);
    const setup: TurnSetup = {
      transport,
      model: this.modelId,
      effort: this.effortLevel,
      executor,
      cadName,
      supported: new Set(tools.map((tool) => tool.name)),
      tools,
      instruction,
    };
    // Until a response names the model, report what will answer: the scripted test model never is Claude.
    turn.model = transport.kind === 'scripted' ? SCRIPTED_MODEL_NAME : setup.model;

    // Thinking blocks are bound to the tool list they were produced with. When that list changes
    // between turns, the history continues without them (once, at this boundary).
    const fingerprint = JSON.stringify(tools);
    if (this.history.length > 0 && this.toolFingerprint !== null && this.toolFingerprint !== fingerprint) {
      this.history = withoutThinking(this.history);
    }
    this.toolFingerprint = fingerprint;
    const history = this.history;
    turn.history = history;

    const context: TurnContext = { cadName, document };
    if (document !== null) {
      if (this.seenDocumentId !== undefined && this.seenDocumentId !== document.id) {
        context.documentSwitched = true;
        this.seenDocumentId = document.id;
        this.seenRevision = document.revision;
      } else {
        this.seenDocumentId = document.id;
        if (this.seenRevision === undefined) this.seenRevision = document.revision;
        else if (this.seenRevision !== document.revision) context.changedSinceRevision = this.seenRevision;
      }
    }

    const content: BetaContentBlockParam[] = [...mapping.blocks];
    if (instruction.length > 0) content.push({ type: 'text', text: instruction });
    content.push({ type: 'text', text: buildContextBlock(context) });
    const userMessage: BetaMessageParam = { role: 'user', content };
    history.push(userMessage);

    const signal = turn.controller.signal;
    for (let call = 0; call < this.maxModelCalls; call += 1) {
      if (signal.aborted) return 'stopped';

      const stream = new TextStream((event) => this.emit(event));
      let response: ModelResponse;
      try {
        response = await raceAbort(
          () =>
            transport.run(
              {
                model: setup.model,
                effort: setup.effort,
                system: SYSTEM_PROMPT,
                tools: setup.tools,
                messages: [...history],
              },
              {
                signal,
                onTextDelta: (delta, blockIndex) => {
                  if (!signal.aborted) stream.delta(typeof delta === 'string' ? delta : '', blockIndex ?? 0);
                },
                onRestart: () => stream.restart(),
              },
            ),
          signal,
        );
      } catch (error) {
        stream.closeAll();
        if (signal.aborted) return 'stopped';
        const description = describeModelError(error);
        this.fail(description.message, description.retryable);
        return 'error';
      }
      if (signal.aborted) {
        stream.closeAll();
        return 'stopped';
      }

      addUsage(turn.usage, response.usage);
      if (typeof response.model === 'string' && response.model.length > 0) turn.model = response.model;
      const blocks = Array.isArray(response.content) ? response.content : [];
      stream.finish(blocks);
      const stopReason = response.stopReason ?? 'end_turn';

      if (stopReason === 'refusal') {
        // A declined reply is never recorded (it may be partial). When nothing else happened in
        // this turn, the declined request is taken back as well, so that rephrasing can succeed.
        if (history[history.length - 1] === userMessage) history.pop();
        this.fail(refusalMessage(response.refusal), false);
        return 'refusal';
      }

      const echoed = echoableContent(blocks);
      if (echoed.length > 0) history.push({ role: 'assistant', content: echoed });
      const toolUses = echoed.filter(isToolUse);

      if (stopReason === 'max_tokens') {
        // A tool input cut off at the output limit can look complete; never run it.
        if (toolUses.length > 0) {
          history.push({
            role: 'user',
            content: toolUses.map((use) => toolResultBlock(use.id, failure(MESSAGES.notRunCutOff))),
          });
        }
        this.fail(MESSAGES.maxTokens, true);
        return 'max_tokens';
      }

      if (toolUses.length > 0) {
        const results = await this.runToolCalls(turn, setup, toolUses);
        // All results of one assistant turn go back in a single user message.
        history.push({ role: 'user', content: results });
        if (signal.aborted) return 'stopped';
        continue;
      }

      if (stopReason === 'pause_turn') continue;
      if (stopReason === 'model_context_window_exceeded') {
        this.fail(MESSAGES.contextFull, false);
        return stopReason;
      }
      return stopReason === 'tool_use' ? 'end_turn' : stopReason;
    }

    this.fail(
      `Stopped after ${this.maxModelCalls} model steps in one turn. Send "continue" if the work is not finished yet.`,
      true,
    );
    return 'error';
  }

  // -------------------------------------------------------------------------------------------
  // Tools
  // -------------------------------------------------------------------------------------------

  /** Runs the tool calls of one assistant turn strictly one after another, in the given order. */
  private async runToolCalls(
    turn: ActiveTurn,
    setup: TurnSetup,
    toolUses: readonly BetaToolUseBlockParam[],
  ): Promise<BetaToolResultBlockParam[]> {
    const results: BetaToolResultBlockParam[] = [];
    for (const use of toolUses) {
      let outcome: ToolOutcome;
      if (turn.controller.signal.aborted) {
        outcome = failure(CANCELLED_MESSAGE);
      } else {
        try {
          outcome = await this.runToolCall(turn, setup, use);
        } catch (error) {
          this.log(`Tool call "${use.name}" failed unexpectedly`, error);
          outcome = failure(`The tool could not be run: ${errorMessage(error)}`);
        }
      }
      results.push(toolResultBlock(use.id, outcome));
    }
    return results;
  }

  private runToolCall(turn: ActiveTurn, setup: TurnSetup, use: BetaToolUseBlockParam): Promise<ToolOutcome> {
    if (use.name === ASK_USER_TOOL) return this.askUser(turn, use);
    if (use.name === UPDATE_PLAN_TOOL) return Promise.resolve(this.updatePlan(use));
    return this.runCadTool(turn, setup, use);
  }

  /** `ask_user`: shows the questions and waits for answerQuestion() (or stop()). */
  private async askUser(turn: ActiveTurn, use: BetaToolUseBlockParam): Promise<ToolOutcome> {
    const parsed = parseAskUser(use.input);
    if (!parsed.ok) {
      return failure(errorResultText('Invalid arguments for ask_user. Fix them and ask again:', parsed.errors));
    }
    const questions = parsed.value;
    const requestId = newId('ask');
    const answers = await new Promise<QuestionAnswer[] | null>((resolve) => {
      turn.pendingQuestion = {
        requestId,
        questions,
        resolve: (value) => {
          turn.pendingQuestion = null;
          resolve(value);
        },
      };
      this.emit({ type: 'question.asked', requestId, questions });
    });
    if (answers === null) return failure(CANCELLED_MESSAGE);

    const byQuestion = new Map(answers.map((entry) => [entry.questionId, entry.answer]));
    const list = questions.map((question) => ({
      question: question.question,
      answer: byQuestion.get(question.id) ?? null,
    }));
    const unanswered = list.some((entry) => entry.answer === null);
    return {
      text: JSON.stringify({
        answers: list,
        ...(unanswered
          ? { note: 'A null answer means the user left that question unanswered; choose a sensible default or ask again.' }
          : {}),
      }),
      isError: false,
    };
  }

  /** `update_plan`: publishes the task list. */
  private updatePlan(use: BetaToolUseBlockParam): ToolOutcome {
    const parsed = parseUpdatePlan(use.input);
    if (!parsed.ok) {
      return failure(errorResultText('Invalid arguments for update_plan. Fix them and send the plan again:', parsed.errors));
    }
    this.emit({ type: 'plan.updated', tasks: parsed.value });
    return { text: JSON.stringify({ ok: true }), isError: false };
  }

  /** A catalog tool: checks, approval when required, then the CAD. */
  private async runCadTool(turn: ActiveTurn, setup: TurnSetup, use: BetaToolUseBlockParam): Promise<ToolOutcome> {
    const name = use.name;
    const signal = turn.controller.signal;
    const executor = setup.executor;
    const tool = isLocalTool(name) ? undefined : this.toolSource.get(name);
    if (tool === undefined) return failure(`Unknown tool "${name}". Only the listed tools exist.`);
    if (executor === null) {
      return failure(`No CAD application is connected, so ${name} cannot run. Ask the user to connect one first.`);
    }
    if (!setup.supported.has(name)) {
      return failure(`${setup.cadName ?? 'The connected CAD'} does not support ${name}. Use one of the listed tools instead.`);
    }
    const check = this.toolSource.validate(name, use.input);
    if (!check.ok) {
      return failure(errorResultText(`Invalid arguments for ${name}. Fix them and call the tool again:`, check.errors));
    }
    const args = check.value;
    const title = tool.title;
    let summary: string;
    try {
      summary = this.toolSource.describe(name, use.input);
    } catch {
      summary = title;
    }

    if (tool.risk !== 'read' && requiresApproval(tool.risk, this.approval)) {
      const risk = tool.risk;
      const requestId = newId('approval');
      const decision = await new Promise<ApprovalDecision | null>((resolve) => {
        turn.pendingApproval = {
          requestId,
          resolve: (value) => {
            turn.pendingApproval = null;
            resolve(value);
          },
        };
        this.emit({ type: 'approval.requested', requestId, callId: use.id, name, title, summary, risk });
      });
      if (decision === null) return failure(CANCELLED_MESSAGE);
      if (decision === 'reject') return failure(DECLINED_MESSAGE);
      if (decision === 'approve-all') this.approval = 'auto';
    }
    if (signal.aborted) return failure(CANCELLED_MESSAGE);

    const current = turn.history === this.history;
    const expectedRevision = tool.risk !== 'read' && current ? this.seenRevision : undefined;
    const options = {
      operationId: newId('op'),
      instruction: setup.instruction,
      signal,
      ...(expectedRevision !== undefined ? { expectedRevision } : {}),
    };

    this.emit({ type: 'tool.started', callId: use.id, name, title, summary, args });
    let raw: unknown;
    try {
      raw = await raceAbort(() => executor.execute(name, args, options), signal);
    } catch (error) {
      if (signal.aborted) {
        this.emit({ type: 'tool.finished', callId: use.id, name, result: null, error: 'Cancelled.' });
        return failure(CANCELLED_RUNNING_MESSAGE);
      }
      const message = errorMessage(error);
      this.emit({ type: 'tool.finished', callId: use.id, name, result: null, error: message });
      return failure(
        `The CAD did not complete ${name}: ${message}. The drawing may or may not have changed; inspect it before continuing.`,
      );
    }

    const parsed = toolResultSchema.safeParse(raw);
    if (!parsed.success) {
      const message = 'The CAD returned a result that could not be read.';
      this.emit({ type: 'tool.finished', callId: use.id, name, result: null, error: message });
      return failure(`${message} The drawing may or may not have changed; inspect it before continuing.`);
    }
    const result: ToolResult = parsed.data;
    // Every result carries the drawing revision after the call: that is what the model has now seen.
    if (turn.history === this.history && Number.isInteger(result.revision) && result.revision >= 0) {
      this.seenRevision = result.revision;
    }
    this.emit({ type: 'tool.finished', callId: use.id, name, result });
    return { text: serializeToolResult(result), isError: !result.success };
  }
}

function normaliseKey(apiKey: string | null | undefined): string | null {
  if (typeof apiKey !== 'string') return null;
  const key = apiKey.trim();
  return key.length > 0 ? key : null;
}

function addUsage(total: TurnUsage, part: TurnUsage | null | undefined): void {
  if (part === null || part === undefined) return;
  const add = (value: unknown): number => (typeof value === 'number' && Number.isFinite(value) ? value : 0);
  total.inputTokens += add(part.inputTokens);
  total.outputTokens += add(part.outputTokens);
  total.cacheReadTokens += add(part.cacheReadTokens);
  total.cacheCreationTokens += add(part.cacheCreationTokens);
}

function toolResultBlock(toolUseId: string, outcome: ToolOutcome): BetaToolResultBlockParam {
  const block: BetaToolResultBlockParam = { type: 'tool_result', tool_use_id: toolUseId, content: outcome.text };
  if (outcome.isError) block.is_error = true;
  return block;
}
