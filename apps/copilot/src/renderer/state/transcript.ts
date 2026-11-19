/**
 * The transcript view model: a pure reducer that folds the main process's {@link TimelineEntry}
 * stream into what the conversation shows (messages, question cards, "CAD activity" groups,
 * approval cards, the task plan, errors and turn footers).
 *
 * Pure and framework-free so it can be unit-tested in Node. Items that do not change keep their
 * object identity, which lets the React components skip re-rendering them while text streams.
 */
import type { ToolResult } from '@cadstudio/cad-protocol';
import type { PlanTask, Question, QuestionAnswer, TurnUsage } from '@cadstudio/copilot-agent';
import type { AttachmentInfo, TimelineEntry } from '../../shared/bridge';

export type ApprovalDecision = 'approve' | 'reject' | 'approve-all';

/** running: waiting for the CAD; done / failed: confirmed by the CAD; stopped: the turn ended first. */
export type ActivityStatus = 'running' | 'done' | 'failed' | 'stopped';

export interface ActivityRow {
  callId: string;
  /** Catalog tool name, e.g. 'create_rectangle'. */
  name: string;
  title: string;
  /** One-line description of the call, written by the main process. */
  summary: string;
  status: ActivityStatus;
  /** The first error, shown on the collapsed row when the call failed. */
  error: string | null;
  warnings: readonly string[];
  errors: readonly string[];
  /** How many objects the CAD confirmed it created / modified / deleted. */
  created: number;
  modified: number;
  deleted: number;
}

export interface UserMessageItem {
  kind: 'user';
  key: string;
  text: string;
  attachments: readonly AttachmentInfo[];
}

export interface AssistantMessageItem {
  kind: 'assistant';
  key: string;
  messageId: string;
  text: string;
  /** True until 'message.done' arrives or the turn ends. */
  streaming: boolean;
}

export interface QuestionItem {
  kind: 'question';
  key: string;
  requestId: string;
  questions: readonly Question[];
  /** pending: waiting for the user; answered: 'question.answered' arrived; cancelled: the turn ended first. */
  status: 'pending' | 'answered' | 'cancelled';
  answers: readonly QuestionAnswer[];
}

export interface ActivityGroupItem {
  kind: 'activity';
  key: string;
  rows: readonly ActivityRow[];
}

export interface ApprovalItem {
  kind: 'approval';
  key: string;
  requestId: string;
  callId: string;
  name: string;
  title: string;
  summary: string;
  risk: 'write' | 'destructive';
  /** null while the card waits for the user (or was cancelled). */
  decision: ApprovalDecision | null;
  /** True when the turn ended before the user decided. */
  cancelled: boolean;
}

export interface PlanItem {
  kind: 'plan';
  key: string;
  tasks: readonly PlanTask[];
}

export interface ErrorItem {
  kind: 'error';
  key: string;
  message: string;
  retryable: boolean;
}

export interface TurnFooterItem {
  kind: 'turn';
  key: string;
  /** True when the user stopped the turn. */
  stopped: boolean;
  /** The model's stop reason ('end_turn', 'max_tokens', 'refusal', ...), when the turn reported one. */
  stopReason: string | null;
  model: string | null;
  usage: TurnUsage | null;
}

export type TranscriptItem =
  | UserMessageItem
  | AssistantMessageItem
  | QuestionItem
  | ActivityGroupItem
  | ApprovalItem
  | PlanItem
  | ErrorItem
  | TurnFooterItem;

/** The plan of the turn that is still running; it joins `items` when the turn ends. */
export interface LivePlan {
  key: string;
  tasks: readonly PlanTask[];
}

export interface TranscriptState {
  /** `seq` of the last applied entry; entries at or below it are duplicates and are ignored. */
  lastSeq: number;
  items: readonly TranscriptItem[];
  livePlan: LivePlan | null;
  /** True between a 'user.message' and the 'turn.finished' / 'turn.stopped' that ends its turn. */
  turnOpen: boolean;
  /**
   * Whitespace-only text received for messages that are not shown yet, by message id. A message
   * becomes an item with its first visible character, so blank text blocks between tool calls do
   * not split a "CAD activity" group.
   */
  pendingText: Readonly<Record<string, string>>;
}

export const INITIAL_TRANSCRIPT: TranscriptState = {
  lastSeq: 0,
  items: [],
  livePlan: null,
  turnOpen: false,
  pendingText: {},
};

/**
 * Copilot-only tools that never reach a CAD program: they surface as question cards and the task
 * plan, not as "CAD activity" rows.
 */
const NON_CAD_TOOLS: ReadonlySet<string> = new Set(['ask_user', 'update_plan']);

const GENERIC_FAILURE = 'The CAD application reported a failure.';
const MISSING_RESULT = 'The call ended without a result.';

function hasVisibleText(text: string): boolean {
  return text.trim().length > 0;
}

function countOf(value: unknown): number {
  return Array.isArray(value) ? value.length : 0;
}

/** Keeps only non-empty strings; tolerates a connector that sends something else. */
function stringList(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is string => typeof entry === 'string' && entry.trim().length > 0);
}

/** 'create_rectangle' -> 'Create rectangle'. Used only when a call finished without a start event. */
export function humanizeToolName(name: string): string {
  const words = name.replace(/[_-]+/g, ' ').trim();
  if (words.length === 0) return 'CAD operation';
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function replaceAt<T>(list: readonly T[], index: number, value: T): T[] {
  const next = list.slice();
  next[index] = value;
  return next;
}

function findLastIndex<T>(list: readonly T[], predicate: (value: T) => boolean): number {
  for (let i = list.length - 1; i >= 0; i -= 1) {
    const value = list[i];
    if (value !== undefined && predicate(value)) return i;
  }
  return -1;
}

function omitKey(record: Readonly<Record<string, string>>, key: string): Readonly<Record<string, string>> {
  if (!(key in record)) return record;
  const next: Record<string, string> = {};
  for (const [k, v] of Object.entries(record)) {
    if (k !== key) next[k] = v;
  }
  return next;
}

function newRow(callId: string, name: string, title: string, summary: string): ActivityRow {
  const cleanTitle = hasVisibleText(title) ? title : humanizeToolName(name);
  return {
    callId,
    name,
    title: cleanTitle,
    summary: hasVisibleText(summary) ? summary : cleanTitle,
    status: 'running',
    error: null,
    warnings: [],
    errors: [],
    created: 0,
    modified: 0,
    deleted: 0,
  };
}

/** Where a tool call's row lives: group index in `items`, row index in the group. */
function locateRow(items: readonly TranscriptItem[], callId: string): { group: number; row: number } | null {
  for (let g = items.length - 1; g >= 0; g -= 1) {
    const item = items[g];
    if (item?.kind !== 'activity') continue;
    const row = item.rows.findIndex((candidate) => candidate.callId === callId);
    if (row >= 0) return { group: g, row };
  }
  return null;
}

function updateRow(
  items: readonly TranscriptItem[],
  at: { group: number; row: number },
  update: (row: ActivityRow) => ActivityRow,
): TranscriptItem[] {
  const group = items[at.group];
  if (group?.kind !== 'activity') return items.slice();
  const current = group.rows[at.row];
  if (current === undefined) return items.slice();
  return replaceAt(items, at.group, { ...group, rows: replaceAt(group.rows, at.row, update(current)) });
}

/** Appends a row to the trailing activity group, or starts a new group when the last item is something else. */
function appendRow(items: readonly TranscriptItem[], row: ActivityRow, seq: number): TranscriptItem[] {
  const lastIndex = items.length - 1;
  const last = items[lastIndex];
  if (last?.kind === 'activity') {
    return replaceAt(items, lastIndex, { ...last, rows: [...last.rows, row] });
  }
  return [...items, { kind: 'activity', key: `activity-${seq}`, rows: [row] }];
}

function finishRow(row: ActivityRow, result: ToolResult | null, error: string | undefined): ActivityRow {
  const explicitError = typeof error === 'string' && hasVisibleText(error) ? error : null;
  if (result === null || typeof result !== 'object') {
    const message = explicitError ?? MISSING_RESULT;
    return { ...row, status: 'failed', error: message, errors: [message] };
  }
  const errors = stringList(result.errors);
  if (explicitError !== null && !errors.includes(explicitError)) errors.unshift(explicitError);
  const succeeded = result.success === true && explicitError === null;
  if (!succeeded && errors.length === 0) errors.push(GENERIC_FAILURE);
  return {
    ...row,
    status: succeeded ? 'done' : 'failed',
    error: succeeded ? null : (errors[0] ?? GENERIC_FAILURE),
    warnings: stringList(result.warnings),
    errors,
    created: countOf(result.createdIds),
    modified: countOf(result.modifiedIds),
    deleted: countOf(result.deletedIds),
  };
}

/**
 * Settles everything that can no longer progress once a turn is over: streaming text is final,
 * running calls are marked stopped, unanswered questions and approvals are cancelled, and the
 * live plan joins the transcript.
 */
function settleTurn(state: TranscriptState): TranscriptState {
  let changed = false;
  const items: TranscriptItem[] = state.items.map((item) => {
    switch (item.kind) {
      case 'assistant':
        if (!item.streaming) return item;
        changed = true;
        return { ...item, streaming: false };
      case 'question':
        if (item.status !== 'pending') return item;
        changed = true;
        return { ...item, status: 'cancelled' };
      case 'approval':
        if (item.decision !== null || item.cancelled) return item;
        changed = true;
        return { ...item, cancelled: true };
      case 'activity': {
        if (!item.rows.some((row) => row.status === 'running')) return item;
        changed = true;
        return {
          ...item,
          rows: item.rows.map((row) => (row.status === 'running' ? { ...row, status: 'stopped' as const } : row)),
        };
      }
      default:
        return item;
    }
  });
  if (state.livePlan !== null) {
    changed = true;
    items.push({ kind: 'plan', key: state.livePlan.key, tasks: state.livePlan.tasks });
  }
  const pendingCleared = Object.keys(state.pendingText).length > 0;
  if (!changed && !pendingCleared && !state.turnOpen) return state;
  return {
    ...state,
    items: changed ? items : state.items,
    livePlan: null,
    turnOpen: false,
    pendingText: pendingCleared ? {} : state.pendingText,
  };
}

/** Ends the turn and records how it ended in one footer (a stop and a finish of one turn share it). */
function endTurn(
  state: TranscriptState,
  seq: number,
  footer: { stopped: boolean; stopReason: string | null; model: string | null; usage: TurnUsage | null },
): TranscriptState {
  const wasOpen = state.turnOpen;
  const settled = settleTurn(state);
  const existingIndex = wasOpen ? -1 : currentTurnFooterIndex(settled.items);
  const existing = settled.items[existingIndex];
  if (existing?.kind === 'turn') {
    // The turn already ended once (Stop, then the model's own finish): one footer, kept last so
    // anything the turn still reported while winding down stays above it.
    const merged: TurnFooterItem = {
      ...existing,
      stopped: existing.stopped || footer.stopped,
      stopReason: footer.stopReason ?? existing.stopReason,
      model: footer.model ?? existing.model,
      usage: footer.usage ?? existing.usage,
    };
    const items = settled.items.filter((_, index) => index !== existingIndex);
    items.push(merged);
    return { ...settled, items };
  }
  return { ...settled, items: [...settled.items, { kind: 'turn', key: `turn-${seq}`, ...footer }] };
}

/** Index of the footer of the most recent turn (nothing but that turn's items after it), or -1. */
function currentTurnFooterIndex(items: readonly TranscriptItem[]): number {
  for (let i = items.length - 1; i >= 0; i -= 1) {
    const item = items[i];
    if (item === undefined || item.kind === 'user') return -1;
    if (item.kind === 'turn') return i;
  }
  return -1;
}

function applyDelta(state: TranscriptState, seq: number, messageId: string, text: string): TranscriptState {
  const index = findLastIndex(state.items, (item) => item.kind === 'assistant' && item.messageId === messageId);
  const existing = state.items[index];
  if (existing?.kind === 'assistant') {
    // A delta for a message that is already complete is a replay; the full text wins.
    if (!existing.streaming || text.length === 0) return state;
    return { ...state, items: replaceAt(state.items, index, { ...existing, text: existing.text + text }) };
  }
  const combined = (state.pendingText[messageId] ?? '') + text;
  if (!hasVisibleText(combined)) {
    return { ...state, pendingText: { ...state.pendingText, [messageId]: combined } };
  }
  return {
    ...state,
    pendingText: omitKey(state.pendingText, messageId),
    items: [...state.items, { kind: 'assistant', key: `message-${seq}`, messageId, text: combined, streaming: true }],
  };
}

function applyDone(state: TranscriptState, seq: number, messageId: string, text: string): TranscriptState {
  const pendingText = omitKey(state.pendingText, messageId);
  const index = findLastIndex(state.items, (item) => item.kind === 'assistant' && item.messageId === messageId);
  const existing = state.items[index];
  if (existing?.kind === 'assistant') {
    // An empty final text never erases what was streamed.
    const finalText = hasVisibleText(text) ? text : existing.text;
    if (!existing.streaming && existing.text === finalText && pendingText === state.pendingText) return state;
    return {
      ...state,
      pendingText,
      items: replaceAt(state.items, index, { ...existing, text: finalText, streaming: false }),
    };
  }
  if (!hasVisibleText(text)) return { ...state, pendingText };
  return {
    ...state,
    pendingText,
    items: [...state.items, { kind: 'assistant', key: `message-${seq}`, messageId, text, streaming: false }],
  };
}

function applyEntryBody(state: TranscriptState, entry: TimelineEntry): TranscriptState {
  const { seq } = entry;
  switch (entry.type) {
    case 'session.reset':
      return INITIAL_TRANSCRIPT;

    case 'user.message': {
      // A turn that never reported its end is settled before the next one starts.
      const base = state.turnOpen || state.livePlan !== null ? settleTurn(state) : state;
      const item: UserMessageItem = {
        kind: 'user',
        key: `user-${seq}`,
        text: entry.text,
        attachments: Array.isArray(entry.attachments) ? entry.attachments : [],
      };
      return { ...base, items: [...base.items, item], turnOpen: true };
    }

    case 'message.delta':
      return applyDelta(state, seq, entry.messageId, entry.text);

    case 'message.done':
      return applyDone(state, seq, entry.messageId, entry.text);

    case 'tool.started': {
      if (NON_CAD_TOOLS.has(entry.name)) return state;
      const located = locateRow(state.items, entry.callId);
      if (located !== null) {
        // A repeated start (for example announced again after an approval) refreshes the wording only.
        const fresh = newRow(entry.callId, entry.name, entry.title, entry.summary);
        return {
          ...state,
          items: updateRow(state.items, located, (row) => ({ ...row, title: fresh.title, summary: fresh.summary })),
        };
      }
      return {
        ...state,
        items: appendRow(state.items, newRow(entry.callId, entry.name, entry.title, entry.summary), seq),
      };
    }

    case 'tool.finished': {
      if (NON_CAD_TOOLS.has(entry.name)) return state;
      const located = locateRow(state.items, entry.callId);
      if (located !== null) {
        return {
          ...state,
          items: updateRow(state.items, located, (row) => finishRow(row, entry.result, entry.error)),
        };
      }
      const row = finishRow(newRow(entry.callId, entry.name, '', ''), entry.result, entry.error);
      return { ...state, items: appendRow(state.items, row, seq) };
    }

    case 'question.asked': {
      if (state.items.some((item) => item.kind === 'question' && item.requestId === entry.requestId)) return state;
      const item: QuestionItem = {
        kind: 'question',
        key: `question-${seq}`,
        requestId: entry.requestId,
        questions: Array.isArray(entry.questions) ? entry.questions : [],
        status: 'pending',
        answers: [],
      };
      return { ...state, items: [...state.items, item] };
    }

    case 'question.answered': {
      const index = findLastIndex(
        state.items,
        (item) => item.kind === 'question' && item.requestId === entry.requestId,
      );
      const existing = state.items[index];
      if (existing?.kind !== 'question') return state;
      const answers = Array.isArray(entry.answers) ? entry.answers : [];
      return { ...state, items: replaceAt(state.items, index, { ...existing, status: 'answered', answers }) };
    }

    case 'approval.requested': {
      if (state.items.some((item) => item.kind === 'approval' && item.requestId === entry.requestId)) return state;
      const item: ApprovalItem = {
        kind: 'approval',
        key: `approval-${seq}`,
        requestId: entry.requestId,
        callId: entry.callId,
        name: entry.name,
        title: hasVisibleText(entry.title) ? entry.title : humanizeToolName(entry.name),
        summary: hasVisibleText(entry.summary) ? entry.summary : humanizeToolName(entry.name),
        risk: entry.risk === 'destructive' ? 'destructive' : 'write',
        decision: null,
        cancelled: false,
      };
      return { ...state, items: [...state.items, item] };
    }

    case 'approval.resolved': {
      const index = findLastIndex(
        state.items,
        (item) => item.kind === 'approval' && item.requestId === entry.requestId,
      );
      const existing = state.items[index];
      if (existing?.kind !== 'approval') return state;
      return {
        ...state,
        items: replaceAt(state.items, index, { ...existing, decision: entry.decision, cancelled: false }),
      };
    }

    case 'plan.updated': {
      const tasks = Array.isArray(entry.tasks) ? entry.tasks : [];
      return { ...state, livePlan: { key: state.livePlan?.key ?? `plan-${seq}`, tasks } };
    }

    case 'error': {
      const item: ErrorItem = {
        kind: 'error',
        key: `error-${seq}`,
        message: hasVisibleText(entry.message) ? entry.message : 'Something went wrong.',
        retryable: entry.retryable === true,
      };
      return { ...state, items: [...state.items, item] };
    }

    case 'turn.finished':
      return endTurn(state, seq, {
        stopped: entry.stopReason === 'stopped',
        stopReason: entry.stopReason,
        model: entry.model,
        usage: entry.usage,
      });

    case 'turn.stopped':
      return endTurn(state, seq, { stopped: true, stopReason: null, model: null, usage: null });

    default:
      // An entry type this build does not know: nothing to show.
      return state;
  }
}

/**
 * Applies one timeline entry. Entries whose `seq` is not greater than the last applied one are
 * duplicates (for example a live entry that the startup snapshot already contained) and are ignored.
 */
export function applyEntry(state: TranscriptState, entry: TimelineEntry): TranscriptState {
  if (!Number.isFinite(entry.seq) || entry.seq <= state.lastSeq) return state;
  const next = applyEntryBody(state, entry);
  return { ...next, lastSeq: entry.seq };
}

/** Applies entries in order (a snapshot's timeline, or one animation frame's worth of live entries). */
export function applyEntries(state: TranscriptState, entries: readonly TimelineEntry[]): TranscriptState {
  let next = state;
  for (const entry of entries) next = applyEntry(next, entry);
  return next;
}

/** Builds the transcript of a startup snapshot. */
export function transcriptFromTimeline(entries: readonly TimelineEntry[]): TranscriptState {
  return applyEntries(INITIAL_TRANSCRIPT, entries);
}

/** True when nothing has been said yet (the welcome / setup cards are shown instead). */
export function isTranscriptEmpty(state: TranscriptState): boolean {
  return state.items.length === 0 && state.livePlan === null;
}

/** The call id of the newest running tool call (its row reads "Working in <CAD>"), or null. */
export function newestRunningCallId(state: TranscriptState): string | null {
  for (let g = state.items.length - 1; g >= 0; g -= 1) {
    const item = state.items[g];
    if (item?.kind !== 'activity') continue;
    for (let r = item.rows.length - 1; r >= 0; r -= 1) {
      const row = item.rows[r];
      if (row?.status === 'running') return row.callId;
    }
  }
  return null;
}

/** Counts for the "Task Progress (done/total)" title. */
export function planProgress(tasks: readonly PlanTask[]): { done: number; total: number } {
  return { done: tasks.filter((task) => task.status === 'done').length, total: tasks.length };
}

/**
 * True while the transcript itself shows that something is in progress or waiting for the user
 * (streaming text, a running call, an open question or approval). When the app is busy and this
 * is false, the conversation shows a "Thinking" indicator instead of dead air.
 */
export function hasVisibleProgress(state: TranscriptState): boolean {
  for (let i = state.items.length - 1; i >= 0; i -= 1) {
    const item = state.items[i];
    if (item === undefined) continue;
    if (item.kind === 'user' || item.kind === 'turn') return false;
    if (item.kind === 'assistant' && item.streaming) return true;
    if (item.kind === 'question' && item.status === 'pending') return true;
    if (item.kind === 'approval' && item.decision === null && !item.cancelled) return true;
    if (item.kind === 'activity' && item.rows.some((row) => row.status === 'running')) return true;
  }
  return false;
}
