/**
 * The conversation as the main process runs it: one CopilotSession, the recorded timeline and the
 * busy flag. Session events and the user's actions become timeline entries that are sent to the
 * renderer live and kept for snapshots. No Electron imports.
 */
import type {
  ApprovalMode,
  Attachment,
  CadToolExecutor,
  Effort,
  ModelId,
  QuestionAnswer,
  SessionEvent,
} from '@cadstudio/copilot-agent';
import type { ActionResult, PickedAttachment, TimelineEntry } from '../shared/bridge';
import { attachmentInfo } from './attachments';
import { errorMessage } from './json-file';
import { TimelineRecorder, type TimelineEvent } from './timeline';
import type { ApprovalDecision } from './validation';

/** The part of CopilotSession the host uses (CopilotSession satisfies it). */
export interface SessionLike {
  on(listener: (event: SessionEvent) => void): () => void;
  send(text: string, attachments?: Attachment[]): Promise<void>;
  answerQuestion(requestId: string, answers: QuestionAnswer[]): void;
  resolveApproval(requestId: string, decision: 'approve' | 'reject' | 'approve-all'): void;
  stop(): void;
  setApiKey(apiKey: string | null): void;
  setModel(model: ModelId): void;
  setEffort(effort: Effort): void;
  setApprovalMode(mode: ApprovalMode): void;
  setExecutor(executor: CadToolExecutor | null): void;
  reset(): void;
}

export const BUSY_MESSAGE = 'CAD Copilot is still working on the previous message.';
export const NOT_READY_MESSAGE = 'The assistant is not ready yet.';
export const EMPTY_MESSAGE = 'Type a message or attach a file first.';

export interface SessionHostOptions {
  /**
   * Creates the session. Called once, by `start()`. When it throws, the host stays usable and
   * every message is refused with the reason.
   */
  createSession(): SessionLike;
  /** A new timeline entry for the renderer. */
  onTimeline(entry: TimelineEntry): void;
  /** The busy flag changed. */
  onBusy(busy: boolean): void;
  readonly log?: (message: string) => void;
  /** Replaces the recorder (tests inject a fixed clock). */
  readonly recorder?: TimelineRecorder;
}

export class SessionHost {
  readonly #options: SessionHostOptions;
  readonly #log: (message: string) => void;
  readonly #recorder: TimelineRecorder;
  #session: SessionLike | null = null;
  #startError: string | null = null;
  #busy = false;
  /** Turns started whose send() has not settled yet (a reset turn counts until it has wound down). */
  #runningTurns = 0;
  /** Identifies the conversation; events of a turn that belongs to an earlier one are dropped. */
  #epoch = 0;
  /** Set while a turn of an earlier conversation (before a reset) is still winding down. */
  #discardSessionEvents = false;
  readonly #pendingQuestions = new Set<string>();
  readonly #pendingApprovals = new Set<string>();

  constructor(options: SessionHostOptions) {
    this.#options = options;
    this.#log = options.log ?? (() => undefined);
    this.#recorder = options.recorder ?? new TimelineRecorder();
  }

  /** Creates the session and starts forwarding its events. Never throws. */
  start(): void {
    if (this.#session !== null) return;
    try {
      const session = this.#options.createSession();
      session.on((event) => this.#handleSessionEvent(event));
      this.#session = session;
      this.#startError = null;
    } catch (error) {
      this.#startError = `The assistant could not be started: ${errorMessage(error)}`;
      this.#log(this.#startError);
    }
  }

  /** True while a turn is running. */
  get busy(): boolean {
    return this.#busy;
  }

  /** The recorded timeline for a snapshot. */
  timeline(): TimelineEntry[] {
    return this.#recorder.snapshot();
  }

  /**
   * Starts a turn with a message that was already validated (see checkMessage). Returns as soon
   * as the turn is accepted; the turn itself reports through timeline entries. Refused while a
   * turn is running.
   */
  sendMessage(text: string, attachments: readonly PickedAttachment[]): ActionResult {
    if (this.#busy) return { ok: false, message: BUSY_MESSAGE };
    const session = this.#session;
    if (session === null) {
      return { ok: false, message: this.#startError ?? NOT_READY_MESSAGE };
    }
    if (text.trim().length === 0 && attachments.length === 0) return { ok: false, message: EMPTY_MESSAGE };

    this.#record({ type: 'user.message', text, attachments: attachments.map(attachmentInfo) });
    this.#runningTurns += 1;
    this.#setBusy(true);
    const epoch = this.#epoch;

    let turn: Promise<void>;
    try {
      turn = session.send(
        text,
        attachments.map(({ name, mediaType, dataBase64 }) => ({ name, mediaType, dataBase64 })),
      );
    } catch (error) {
      turn = Promise.reject(error instanceof Error ? error : new Error(String(error)));
    }
    turn
      .catch((error: unknown) => {
        // send() never rejects by contract; if it does, the turn still ends visibly.
        this.#log(`The turn failed unexpectedly: ${errorMessage(error)}`);
        if (epoch === this.#epoch) {
          this.#record({ type: 'error', message: `The assistant stopped unexpectedly: ${errorMessage(error)}`, retryable: true });
        }
      })
      .finally(() => this.#turnSettled());
    return { ok: true };
  }

  /** Answers a pending question card. Unknown or already answered requests are ignored. */
  answerQuestion(requestId: string, answers: readonly QuestionAnswer[]): void {
    if (!this.#pendingQuestions.delete(requestId)) {
      this.#log(`Ignored an answer to question request ${requestId}: it is not pending.`);
      return;
    }
    const given = answers.map(({ questionId, answer }) => ({ questionId, answer }));
    this.#record({ type: 'question.answered', requestId, answers: given });
    this.#callSession('answerQuestion', (session) => session.answerQuestion(requestId, given));
  }

  /** Resolves a pending approval card. Unknown or already resolved requests are ignored. */
  resolveApproval(requestId: string, decision: ApprovalDecision): void {
    if (!this.#pendingApprovals.delete(requestId)) {
      this.#log(`Ignored a decision for approval request ${requestId}: it is not pending.`);
      return;
    }
    this.#record({ type: 'approval.resolved', requestId, decision });
    this.#callSession('resolveApproval', (session) => session.resolveApproval(requestId, decision));
  }

  /** Stops the running turn; does nothing when no turn is running. */
  stop(): void {
    if (!this.#busy || this.#discardSessionEvents) return;
    this.#record({ type: 'turn.stopped' });
    this.#pendingQuestions.clear();
    this.#pendingApprovals.clear();
    this.#callSession('stop', (session) => session.stop());
  }

  /**
   * Stops any running turn and starts a new conversation. The timeline restarts with a
   * 'session.reset' entry; whatever the stopped turn still emits is dropped, and the busy flag
   * clears once that turn has wound down.
   */
  reset(): void {
    this.#epoch += 1;
    this.#discardSessionEvents = this.#runningTurns > 0;
    this.#pendingQuestions.clear();
    this.#pendingApprovals.clear();
    this.#callSession('reset', (session) => session.reset());
    this.#emitTimeline(this.#recorder.reset());
  }

  setExecutor(executor: CadToolExecutor | null): void {
    this.#callSession('setExecutor', (session) => session.setExecutor(executor));
  }

  setApiKey(apiKey: string | null): void {
    this.#callSession('setApiKey', (session) => session.setApiKey(apiKey));
  }

  /** Applies changed model settings to the session (they take effect on the next turn). */
  applySettings(settings: { model?: ModelId; effort?: Effort; approvalMode?: ApprovalMode }): void {
    const { model, effort, approvalMode } = settings;
    if (model !== undefined) this.#callSession('setModel', (session) => session.setModel(model));
    if (effort !== undefined) this.#callSession('setEffort', (session) => session.setEffort(effort));
    if (approvalMode !== undefined) {
      this.#callSession('setApprovalMode', (session) => session.setApprovalMode(approvalMode));
    }
  }

  /** Stops the running turn without recording anything (the app is quitting). */
  dispose(): void {
    if (this.#busy) this.#callSession('stop', (session) => session.stop());
  }

  // -------------------------------------------------------------------------------------------

  #handleSessionEvent(event: SessionEvent): void {
    if (this.#discardSessionEvents) return;
    if (event.type === 'question.asked') this.#pendingQuestions.add(event.requestId);
    else if (event.type === 'approval.requested') this.#pendingApprovals.add(event.requestId);
    else if (event.type === 'turn.finished') {
      this.#pendingQuestions.clear();
      this.#pendingApprovals.clear();
    }
    this.#record(event);
  }

  #turnSettled(): void {
    this.#runningTurns = Math.max(0, this.#runningTurns - 1);
    if (this.#runningTurns > 0) return;
    this.#discardSessionEvents = false;
    this.#pendingQuestions.clear();
    this.#pendingApprovals.clear();
    this.#setBusy(false);
  }

  #record(event: TimelineEvent): void {
    this.#emitTimeline(this.#recorder.record(event));
  }

  #emitTimeline(entry: TimelineEntry): void {
    try {
      this.#options.onTimeline(entry);
    } catch (error) {
      this.#log(`Sending a timeline entry failed: ${errorMessage(error)}`);
    }
  }

  #setBusy(busy: boolean): void {
    if (this.#busy === busy) return;
    this.#busy = busy;
    try {
      this.#options.onBusy(busy);
    } catch (error) {
      this.#log(`Sending the busy state failed: ${errorMessage(error)}`);
    }
  }

  #callSession(what: string, call: (session: SessionLike) => void): void {
    const session = this.#session;
    if (session === null) return;
    try {
      call(session);
    } catch (error) {
      this.#log(`CopilotSession.${what} failed: ${errorMessage(error)}`);
    }
  }
}
