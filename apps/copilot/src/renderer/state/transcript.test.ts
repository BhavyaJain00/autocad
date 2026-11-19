import { describe, it, expect } from 'vitest';
import type { ToolResult } from '@cadstudio/cad-protocol';
import type { PlanTask, Question, TurnUsage } from '@cadstudio/copilot-agent';
import type { TimelineEntry } from '../../shared/bridge';
import {
  INITIAL_TRANSCRIPT,
  applyEntries,
  applyEntry,
  hasVisibleProgress,
  humanizeToolName,
  isTranscriptEmpty,
  newestRunningCallId,
  planProgress,
  transcriptFromTimeline,
} from './transcript';
import type {
  ActivityGroupItem,
  ApprovalItem,
  AssistantMessageItem,
  QuestionItem,
  TranscriptItem,
  TranscriptState,
  TurnFooterItem,
} from './transcript';

// ---------------------------------------------------------------------------------------------
// Entry builders
// ---------------------------------------------------------------------------------------------

const AT = '2026-11-13T10:00:00.000Z';

/** Distributes Omit over the TimelineEntry union so each body keeps its own fields. */
type EntryBody = TimelineEntry extends infer E ? (E extends TimelineEntry ? Omit<E, 'seq' | 'at'> : never) : never;

function entry(seq: number, body: EntryBody): TimelineEntry {
  return { seq, at: AT, ...body } as TimelineEntry;
}

/** Numbers the bodies 1, 2, 3, ... in order. */
function timeline(...bodies: EntryBody[]): TimelineEntry[] {
  return bodies.map((body, index) => entry(index + 1, body));
}

const user = (text: string): EntryBody => ({ type: 'user.message', text, attachments: [] });
const delta = (messageId: string, text: string): EntryBody => ({ type: 'message.delta', messageId, text });
const done = (messageId: string, text: string): EntryBody => ({ type: 'message.done', messageId, text });
const started = (callId: string, name = 'create_rectangle', summary = `Run ${callId}`): EntryBody => ({
  type: 'tool.started',
  callId,
  name,
  title: 'Create rectangle',
  summary,
  args: {},
});

function result(overrides: Partial<ToolResult> = {}): ToolResult {
  return {
    success: true,
    createdIds: [],
    modifiedIds: [],
    deletedIds: [],
    warnings: [],
    errors: [],
    revision: 1,
    ...overrides,
  };
}

const finished = (
  callId: string,
  toolResult: ToolResult | null = result(),
  error?: string,
  name = 'create_rectangle',
): EntryBody => ({ type: 'tool.finished', callId, name, result: toolResult, ...(error === undefined ? {} : { error }) });

const USAGE: TurnUsage = { inputTokens: 1200, outputTokens: 300, cacheReadTokens: 5000, cacheCreationTokens: 0 };
const turnFinished = (stopReason = 'end_turn'): EntryBody => ({
  type: 'turn.finished',
  stopReason,
  usage: USAGE,
  model: 'claude-opus-5-5',
});
const turnStopped: EntryBody = { type: 'turn.stopped' };

const QUESTIONS: Question[] = [
  {
    id: 'q-thickness',
    header: 'WALL THICKNESS',
    question: 'How thick should the walls be?',
    options: [{ label: '9 in', recommended: true }, { label: '4.5 in' }],
    allowCustom: true,
  },
  {
    id: 'q-door',
    header: 'DOOR',
    question: 'Where does the door go?',
    options: [{ label: 'South wall' }, { label: 'East wall' }],
    allowCustom: false,
  },
];

const asked = (requestId: string): EntryBody => ({ type: 'question.asked', requestId, questions: QUESTIONS });
const approvalRequested = (requestId: string, risk: 'write' | 'destructive' = 'write'): EntryBody => ({
  type: 'approval.requested',
  requestId,
  callId: `call-${requestId}`,
  name: risk === 'destructive' ? 'delete_entities' : 'move_entities',
  title: risk === 'destructive' ? 'Delete objects' : 'Move objects',
  summary: risk === 'destructive' ? 'Delete 3 objects' : 'Move 2 objects by (3, 0) ft',
  risk,
});

const task = (id: string, status: PlanTask['status']): PlanTask => ({ id, title: `Task ${id}`, status });
const plan = (...tasks: PlanTask[]): EntryBody => ({ type: 'plan.updated', tasks });

// ---------------------------------------------------------------------------------------------
// Item accessors
// ---------------------------------------------------------------------------------------------

function kinds(state: TranscriptState): string[] {
  return state.items.map((item) => item.kind);
}

function only<K extends TranscriptItem['kind']>(state: TranscriptState, kind: K): Extract<TranscriptItem, { kind: K }>[] {
  return state.items.filter((item): item is Extract<TranscriptItem, { kind: K }> => item.kind === kind);
}

function firstOf<K extends TranscriptItem['kind']>(state: TranscriptState, kind: K): Extract<TranscriptItem, { kind: K }> {
  const found = only(state, kind)[0];
  if (found === undefined) throw new Error(`no ${kind} item in the transcript`);
  return found;
}

function rowsOf(state: TranscriptState): ActivityGroupItem['rows'][number][] {
  return only(state, 'activity').flatMap((group) => [...group.rows]);
}

// ---------------------------------------------------------------------------------------------

describe('transcript reducer: messages', () => {
  it('starts empty', () => {
    expect(INITIAL_TRANSCRIPT.items).toEqual([]);
    expect(INITIAL_TRANSCRIPT.lastSeq).toBe(0);
    expect(isTranscriptEmpty(INITIAL_TRANSCRIPT)).toBe(true);
    expect(transcriptFromTimeline([])).toEqual(INITIAL_TRANSCRIPT);
  });

  it('adds a user message with its attachments and opens a turn', () => {
    const state = transcriptFromTimeline([
      entry(1, {
        type: 'user.message',
        text: 'Draw a room',
        attachments: [{ name: 'plan.png', mediaType: 'image/png', sizeBytes: 2048 }],
      }),
    ]);
    const message = firstOf(state, 'user');
    expect(message.text).toBe('Draw a room');
    expect(message.attachments).toEqual([{ name: 'plan.png', mediaType: 'image/png', sizeBytes: 2048 }]);
    expect(state.turnOpen).toBe(true);
    expect(state.lastSeq).toBe(1);
    expect(isTranscriptEmpty(state)).toBe(false);
  });

  it('concatenates streaming deltas into one assistant message', () => {
    const state = transcriptFromTimeline(timeline(user('hi'), delta('m1', 'Hel'), delta('m1', 'lo'), delta('m1', ' there')));
    const messages = only(state, 'assistant');
    expect(messages).toHaveLength(1);
    expect(messages[0]?.text).toBe('Hello there');
    expect(messages[0]?.streaming).toBe(true);
    expect(messages[0]?.messageId).toBe('m1');
  });

  it("replaces the streamed text with the final text on 'message.done'", () => {
    const state = transcriptFromTimeline(timeline(user('hi'), delta('m1', 'Helo'), done('m1', 'Hello.')));
    const message = firstOf(state, 'assistant');
    expect(message.text).toBe('Hello.');
    expect(message.streaming).toBe(false);
  });

  it("creates the message from 'message.done' alone", () => {
    const state = transcriptFromTimeline(timeline(user('hi'), done('m1', 'All done.')));
    const message = firstOf(state, 'assistant');
    expect(message.text).toBe('All done.');
    expect(message.streaming).toBe(false);
  });

  it('continues a coalesced snapshot delta with live deltas of the same message', () => {
    // The snapshot holds one delta with the text so far; live deltas then carry on.
    const snapshot = transcriptFromTimeline([entry(1, user('hi')), entry(7, delta('m1', 'The room is '))]);
    expect(firstOf(snapshot, 'assistant').text).toBe('The room is ');
    const live = applyEntries(snapshot, [entry(8, delta('m1', '12 ft ')), entry(9, delta('m1', 'wide.'))]);
    expect(only(live, 'assistant')).toHaveLength(1);
    expect(firstOf(live, 'assistant').text).toBe('The room is 12 ft wide.');
    expect(firstOf(live, 'assistant').streaming).toBe(true);
    const complete = applyEntry(live, entry(10, done('m1', 'The room is 12 ft wide.')));
    expect(firstOf(complete, 'assistant').text).toBe('The room is 12 ft wide.');
    expect(firstOf(complete, 'assistant').streaming).toBe(false);
  });

  it('keeps the key of a message stable while it streams', () => {
    const first = transcriptFromTimeline(timeline(user('hi'), delta('m1', 'a')));
    const second = applyEntry(first, entry(3, delta('m1', 'b')));
    expect(firstOf(second, 'assistant').key).toBe(firstOf(first, 'assistant').key);
  });

  it('keeps separate messages separate and in order', () => {
    const state = transcriptFromTimeline(
      timeline(user('hi'), delta('m1', 'First'), done('m1', 'First'), delta('m2', 'Second'), delta('m1', ' ignored')),
    );
    const messages = only(state, 'assistant');
    expect(messages.map((message) => message.text)).toEqual(['First', 'Second']);
  });

  it('ignores a delta for a message that is already complete', () => {
    const state = transcriptFromTimeline(timeline(user('hi'), done('m1', 'Final.'), delta('m1', ' extra')));
    expect(firstOf(state, 'assistant').text).toBe('Final.');
  });

  it('does not erase streamed text when the final text is empty', () => {
    const state = transcriptFromTimeline(timeline(user('hi'), delta('m1', 'Partial'), done('m1', '')));
    const message = firstOf(state, 'assistant');
    expect(message.text).toBe('Partial');
    expect(message.streaming).toBe(false);
  });

  it('holds back whitespace-only text until something visible arrives', () => {
    const blank = transcriptFromTimeline(timeline(user('hi'), delta('m1', '\n'), delta('m1', '  ')));
    expect(only(blank, 'assistant')).toHaveLength(0);
    const visible = applyEntry(blank, entry(4, delta('m1', 'Hi')));
    expect(firstOf(visible, 'assistant').text).toBe('\n  Hi');
    expect(visible.pendingText).toEqual({});
  });

  it('never shows a message whose whole text is blank', () => {
    const state = transcriptFromTimeline(timeline(user('hi'), delta('m1', '\n\n'), done('m1', '\n\n')));
    expect(only(state, 'assistant')).toHaveLength(0);
    expect(state.pendingText).toEqual({});
  });
});

describe('transcript reducer: duplicates and reset', () => {
  it('ignores entries whose seq is not greater than the last applied one', () => {
    const base = transcriptFromTimeline(timeline(user('hi'), delta('m1', 'Hello')));
    const replayed = applyEntries(base, [entry(1, user('hi')), entry(2, delta('m1', 'Hello'))]);
    expect(replayed).toBe(base);
    expect(only(replayed, 'user')).toHaveLength(1);
    expect(firstOf(replayed, 'assistant').text).toBe('Hello');
  });

  it('returns the same state object for a duplicate', () => {
    const base = transcriptFromTimeline(timeline(user('hi')));
    expect(applyEntry(base, entry(1, user('again')))).toBe(base);
    expect(applyEntry(base, entry(0, user('again')))).toBe(base);
  });

  it('applies the new part of a batch that overlaps the snapshot', () => {
    const base = transcriptFromTimeline(timeline(user('hi'), delta('m1', 'A')));
    const next = applyEntries(base, [entry(2, delta('m1', 'A')), entry(3, delta('m1', 'B'))]);
    expect(firstOf(next, 'assistant').text).toBe('AB');
    expect(next.lastSeq).toBe(3);
  });

  it('ignores entries with a non-finite seq', () => {
    const base = transcriptFromTimeline(timeline(user('hi')));
    expect(applyEntry(base, entry(Number.NaN, user('x')))).toBe(base);
  });

  it("clears everything on 'session.reset' and keeps counting seq", () => {
    const busy = transcriptFromTimeline(
      timeline(user('hi'), delta('m1', 'Hello'), started('c1'), plan(task('t1', 'in_progress')), asked('r1')),
    );
    const reset = applyEntry(busy, entry(6, { type: 'session.reset' }));
    expect(reset.items).toEqual([]);
    expect(reset.livePlan).toBeNull();
    expect(reset.turnOpen).toBe(false);
    expect(reset.pendingText).toEqual({});
    expect(reset.lastSeq).toBe(6);
    expect(isTranscriptEmpty(reset)).toBe(true);
    // Entries from before the reset that arrive late are still duplicates.
    expect(applyEntry(reset, entry(5, delta('m1', 'late')))).toBe(reset);
    const after = applyEntry(reset, entry(7, user('new chat')));
    expect(kinds(after)).toEqual(['user']);
  });

  it('skips unknown entry types but still advances seq', () => {
    const base = transcriptFromTimeline(timeline(user('hi')));
    const unknown = { seq: 2, at: AT, type: 'something.new', value: 1 } as unknown as TimelineEntry;
    const next = applyEntry(base, unknown);
    expect(next.items).toBe(base.items);
    expect(next.lastSeq).toBe(2);
  });
});

describe('transcript reducer: CAD activity', () => {
  it('groups consecutive tool calls into one activity group', () => {
    const state = transcriptFromTimeline(timeline(user('draw'), started('c1'), finished('c1'), started('c2'), started('c3')));
    const groups = only(state, 'activity');
    expect(groups).toHaveLength(1);
    expect(groups[0]?.rows.map((row) => row.callId)).toEqual(['c1', 'c2', 'c3']);
    expect(groups[0]?.rows.map((row) => row.status)).toEqual(['done', 'running', 'running']);
  });

  it('starts a new group after assistant text', () => {
    const state = transcriptFromTimeline(
      timeline(user('draw'), started('c1'), finished('c1'), delta('m1', 'Next step'), done('m1', 'Next step'), started('c2')),
    );
    expect(kinds(state)).toEqual(['user', 'activity', 'assistant', 'activity']);
  });

  it('does not split a group for a blank text block or a plan update', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('draw'),
        started('c1'),
        finished('c1'),
        delta('m1', '\n'),
        done('m1', '\n'),
        plan(task('t1', 'done')),
        started('c2'),
      ),
    );
    expect(kinds(state)).toEqual(['user', 'activity']);
    expect(rowsOf(state)).toHaveLength(2);
  });

  it('records what the CAD confirmed when a call finishes', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('draw'),
        started('c1', 'create_rectangle', 'Create a 12 x 14 ft rectangle'),
        finished('c1', result({ createdIds: ['a', 'b'], modifiedIds: ['c'], deletedIds: [], warnings: ['Layer was locked'] })),
      ),
    );
    const row = rowsOf(state)[0];
    expect(row?.status).toBe('done');
    expect(row?.summary).toBe('Create a 12 x 14 ft rectangle');
    expect(row?.title).toBe('Create rectangle');
    expect(row?.created).toBe(2);
    expect(row?.modified).toBe(1);
    expect(row?.deleted).toBe(0);
    expect(row?.warnings).toEqual(['Layer was locked']);
    expect(row?.error).toBeNull();
    expect(row?.errors).toEqual([]);
  });

  it('marks a call failed with the first error when the CAD refuses it', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('draw'),
        started('c1'),
        finished('c1', result({ success: false, errors: ['The drawing changed', 'Re-inspect first'] })),
      ),
    );
    const row = rowsOf(state)[0];
    expect(row?.status).toBe('failed');
    expect(row?.error).toBe('The drawing changed');
    expect(row?.errors).toEqual(['The drawing changed', 'Re-inspect first']);
  });

  it('marks a call failed when there is no result, using the transport error', () => {
    const state = transcriptFromTimeline(timeline(user('draw'), started('c1'), finished('c1', null, 'The CAD did not answer')));
    const row = rowsOf(state)[0];
    expect(row?.status).toBe('failed');
    expect(row?.error).toBe('The CAD did not answer');
    expect(row?.created).toBe(0);
  });

  it('gives a failed call without any message a readable error', () => {
    const noResult = rowsOf(transcriptFromTimeline(timeline(user('x'), started('c1'), finished('c1', null))))[0];
    expect(noResult?.status).toBe('failed');
    expect(noResult?.error).toMatch(/without a result/i);
    const noErrors = rowsOf(
      transcriptFromTimeline(timeline(user('x'), started('c1'), finished('c1', result({ success: false })))),
    )[0];
    expect(noErrors?.status).toBe('failed');
    expect(noErrors?.error).toMatch(/failure/i);
  });

  it('treats an explicit error as a failure even when the result says success', () => {
    const row = rowsOf(
      transcriptFromTimeline(timeline(user('x'), started('c1'), finished('c1', result({ success: true }), 'Rejected by the user'))),
    )[0];
    expect(row?.status).toBe('failed');
    expect(row?.error).toBe('Rejected by the user');
  });

  it('tolerates a malformed result from a connector', () => {
    const broken = { success: true, revision: 3 } as unknown as ToolResult;
    const row = rowsOf(transcriptFromTimeline(timeline(user('x'), started('c1'), finished('c1', broken))))[0];
    expect(row?.status).toBe('done');
    expect(row?.created).toBe(0);
    expect(row?.warnings).toEqual([]);
  });

  it('creates a row for a call that finished without a start event', () => {
    const state = transcriptFromTimeline(timeline(user('x'), finished('c9', result({ createdIds: ['a'] }), undefined, 'create_circle')));
    const row = rowsOf(state)[0];
    expect(row?.callId).toBe('c9');
    expect(row?.status).toBe('done');
    expect(row?.title).toBe('Create circle');
    expect(row?.summary).toBe('Create circle');
    expect(row?.created).toBe(1);
  });

  it('finishes the right row when calls overlap, across groups', () => {
    const state = transcriptFromTimeline(
      timeline(user('x'), started('c1'), delta('m1', 'meanwhile'), started('c2'), finished('c1'), finished('c2', result({ success: false, errors: ['no'] }))),
    );
    const rows = rowsOf(state);
    expect(rows.map((row) => [row.callId, row.status])).toEqual([
      ['c1', 'done'],
      ['c2', 'failed'],
    ]);
    expect(only(state, 'activity')).toHaveLength(2);
  });

  it('does not duplicate a row when a call is announced twice', () => {
    const state = transcriptFromTimeline(
      timeline(user('x'), started('c1', 'move_entities', 'Move 2 objects'), started('c1', 'move_entities', 'Move 2 objects by 3 ft')),
    );
    expect(rowsOf(state)).toHaveLength(1);
    expect(rowsOf(state)[0]?.summary).toBe('Move 2 objects by 3 ft');
  });

  it('never lists the copilot-only tools as CAD activity', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('x'),
        started('c1', 'ask_user'),
        finished('c1', null, undefined, 'ask_user'),
        started('c2', 'update_plan'),
        finished('c2', result(), undefined, 'update_plan'),
      ),
    );
    expect(only(state, 'activity')).toHaveLength(0);
  });

  it('reports the newest running call', () => {
    const none = transcriptFromTimeline(timeline(user('x'), started('c1'), finished('c1')));
    expect(newestRunningCallId(none)).toBeNull();
    const two = transcriptFromTimeline(timeline(user('x'), started('c1'), started('c2'), delta('m1', 'text'), started('c3'), finished('c3')));
    expect(newestRunningCallId(two)).toBe('c2');
    expect(newestRunningCallId(INITIAL_TRANSCRIPT)).toBeNull();
  });

  it('humanizes tool names', () => {
    expect(humanizeToolName('create_rectangle')).toBe('Create rectangle');
    expect(humanizeToolName('zoom-extents')).toBe('Zoom extents');
    expect(humanizeToolName('')).toBe('CAD operation');
  });
});

describe('transcript reducer: questions', () => {
  it('adds a pending question card', () => {
    const state = transcriptFromTimeline(timeline(user('x'), asked('r1')));
    const card = firstOf(state, 'question');
    expect(card.status).toBe('pending');
    expect(card.requestId).toBe('r1');
    expect(card.questions).toHaveLength(2);
    expect(card.answers).toEqual([]);
  });

  it('marks the card answered with the chosen answers', () => {
    const answers = [
      { questionId: 'q-thickness', answer: '9 in' },
      { questionId: 'q-door', answer: 'South wall' },
    ];
    const state = transcriptFromTimeline(timeline(user('x'), asked('r1'), { type: 'question.answered', requestId: 'r1', answers }));
    const card = firstOf(state, 'question');
    expect(card.status).toBe('answered');
    expect(card.answers).toEqual(answers);
  });

  it('answers only the matching card', () => {
    const state = transcriptFromTimeline(
      timeline(user('x'), asked('r1'), asked('r2'), {
        type: 'question.answered',
        requestId: 'r2',
        answers: [{ questionId: 'q-door', answer: 'East wall' }],
      }),
    );
    const cards = only(state, 'question');
    expect(cards.map((card: QuestionItem) => card.status)).toEqual(['pending', 'answered']);
  });

  it('ignores an answer for an unknown request and a repeated ask', () => {
    const base = transcriptFromTimeline(timeline(user('x'), asked('r1')));
    const stray = applyEntry(base, entry(3, { type: 'question.answered', requestId: 'nope', answers: [] }));
    expect(stray.items).toBe(base.items);
    const repeated = applyEntry(stray, entry(4, asked('r1')));
    expect(only(repeated, 'question')).toHaveLength(1);
  });
});

describe('transcript reducer: approvals', () => {
  it('adds a pending approval card with its risk', () => {
    const state = transcriptFromTimeline(timeline(user('x'), approvalRequested('a1', 'destructive')));
    const card = firstOf(state, 'approval');
    expect(card.decision).toBeNull();
    expect(card.cancelled).toBe(false);
    expect(card.risk).toBe('destructive');
    expect(card.summary).toBe('Delete 3 objects');
    expect(card.title).toBe('Delete objects');
    expect(card.callId).toBe('call-a1');
  });

  it.each(['approve', 'reject', 'approve-all'] as const)("records the '%s' decision", (decision) => {
    const state = transcriptFromTimeline(
      timeline(user('x'), approvalRequested('a1'), { type: 'approval.resolved', requestId: 'a1', decision }),
    );
    expect(firstOf(state, 'approval').decision).toBe(decision);
  });

  it('keeps an approval card between activity groups in order', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('x'),
        started('c1'),
        finished('c1'),
        approvalRequested('a1'),
        { type: 'approval.resolved', requestId: 'a1', decision: 'approve' },
        started('c2'),
      ),
    );
    expect(kinds(state)).toEqual(['user', 'activity', 'approval', 'activity']);
  });

  it('ignores a resolution for an unknown request and a repeated request', () => {
    const base = transcriptFromTimeline(timeline(user('x'), approvalRequested('a1')));
    const stray = applyEntry(base, entry(3, { type: 'approval.resolved', requestId: 'zzz', decision: 'approve' }));
    expect(stray.items).toBe(base.items);
    expect(only(applyEntry(stray, entry(4, approvalRequested('a1'))), 'approval')).toHaveLength(1);
  });
});

describe('transcript reducer: plan', () => {
  it('keeps the plan of a running turn out of the transcript', () => {
    const state = transcriptFromTimeline(timeline(user('x'), plan(task('t1', 'in_progress'), task('t2', 'pending'))));
    expect(only(state, 'plan')).toHaveLength(0);
    expect(state.livePlan?.tasks.map((item) => item.status)).toEqual(['in_progress', 'pending']);
    expect(isTranscriptEmpty(state)).toBe(false);
  });

  it('replaces the live plan with each update and keeps its key', () => {
    const first = transcriptFromTimeline(timeline(user('x'), plan(task('t1', 'in_progress'), task('t2', 'pending'))));
    const second = applyEntry(first, entry(3, plan(task('t1', 'done'), task('t2', 'in_progress'))));
    expect(second.livePlan?.key).toBe(first.livePlan?.key);
    expect(second.livePlan?.tasks.map((item) => item.status)).toEqual(['done', 'in_progress']);
  });

  it('moves the plan into the transcript, before the footer, when the turn ends', () => {
    const state = transcriptFromTimeline(
      timeline(user('x'), plan(task('t1', 'in_progress')), started('c1'), finished('c1'), plan(task('t1', 'done')), done('m1', 'Done.'), turnFinished()),
    );
    expect(kinds(state)).toEqual(['user', 'activity', 'assistant', 'plan', 'turn']);
    expect(state.livePlan).toBeNull();
    expect(firstOf(state, 'plan').tasks).toEqual([task('t1', 'done')]);
  });

  it('gives each turn its own plan', () => {
    const state = transcriptFromTimeline(
      timeline(user('one'), plan(task('a', 'done')), turnFinished(), user('two'), plan(task('b', 'in_progress'))),
    );
    expect(only(state, 'plan')).toHaveLength(1);
    expect(firstOf(state, 'plan').tasks[0]?.id).toBe('a');
    expect(state.livePlan?.tasks[0]?.id).toBe('b');
    expect(state.livePlan?.key).not.toBe(firstOf(state, 'plan').key);
  });

  it('counts finished tasks', () => {
    expect(planProgress([task('a', 'done'), task('b', 'in_progress'), task('c', 'done'), task('d', 'failed')])).toEqual({
      done: 2,
      total: 4,
    });
    expect(planProgress([])).toEqual({ done: 0, total: 0 });
  });
});

describe('transcript reducer: errors and turn endings', () => {
  it('adds an inline error notice', () => {
    const state = transcriptFromTimeline(timeline(user('x'), { type: 'error', message: 'Rate limited', retryable: true }));
    const notice = firstOf(state, 'error');
    expect(notice.message).toBe('Rate limited');
    expect(notice.retryable).toBe(true);
    expect(state.turnOpen).toBe(true);
  });

  it('adds a footer with model and usage when the turn finishes', () => {
    const state = transcriptFromTimeline(timeline(user('x'), done('m1', 'Hi'), turnFinished()));
    const footer = firstOf(state, 'turn');
    expect(footer.model).toBe('claude-opus-5-5');
    expect(footer.usage).toEqual(USAGE);
    expect(footer.stopReason).toBe('end_turn');
    expect(footer.stopped).toBe(false);
    expect(state.turnOpen).toBe(false);
  });

  it("shows 'turn.stopped' as a stopped footer and settles what was in flight", () => {
    const state = transcriptFromTimeline(
      timeline(
        user('x'),
        delta('m1', 'Working on'),
        started('c1'),
        asked('r1'),
        approvalRequested('a1'),
        plan(task('t1', 'in_progress')),
        turnStopped,
      ),
    );
    expect(firstOf(state, 'turn').stopped).toBe(true);
    expect(firstOf(state, 'turn').model).toBeNull();
    expect(firstOf(state, 'assistant').streaming).toBe(false);
    expect(rowsOf(state)[0]?.status).toBe('stopped');
    expect(firstOf(state, 'question').status).toBe('cancelled');
    const approval: ApprovalItem = firstOf(state, 'approval');
    expect(approval.cancelled).toBe(true);
    expect(approval.decision).toBeNull();
    expect(kinds(state).slice(-2)).toEqual(['plan', 'turn']);
    expect(state.turnOpen).toBe(false);
    expect(newestRunningCallId(state)).toBeNull();
  });

  it('merges a stop and a finish of the same turn into one footer (either order)', () => {
    const stopFirst = transcriptFromTimeline(timeline(user('x'), delta('m1', 'Hi'), turnStopped, turnFinished('stopped')));
    const footers: TurnFooterItem[] = only(stopFirst, 'turn');
    expect(footers).toHaveLength(1);
    expect(footers[0]?.stopped).toBe(true);
    expect(footers[0]?.model).toBe('claude-opus-5-5');
    expect(footers[0]?.usage).toEqual(USAGE);

    const finishFirst = transcriptFromTimeline(timeline(user('x'), delta('m1', 'Hi'), turnFinished('stopped'), turnStopped));
    expect(only(finishFirst, 'turn')).toHaveLength(1);
    expect(firstOf(finishFirst, 'turn').stopped).toBe(true);
    expect(firstOf(finishFirst, 'turn').stopReason).toBe('stopped');
    expect(firstOf(finishFirst, 'turn').usage).toEqual(USAGE);
  });

  it('keeps one footer, last, when a stopped turn still reports while it winds down', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('x'),
        delta('m1', 'Working'),
        started('c1'),
        turnStopped,
        // The session winds down after Stop: the call returns, an error is reported, the turn finishes.
        finished('c1', result({ createdIds: ['a'] })),
        delta('m1', ' late text'),
        { type: 'error', message: 'Request aborted', retryable: false },
        turnFinished('stopped'),
      ),
    );
    expect(kinds(state)).toEqual(['user', 'assistant', 'activity', 'error', 'turn']);
    expect(only(state, 'turn')).toHaveLength(1);
    expect(firstOf(state, 'turn').stopped).toBe(true);
    expect(firstOf(state, 'turn').usage).toEqual(USAGE);
    // What the CAD confirmed after the stop is still reported truthfully.
    expect(rowsOf(state)[0]?.status).toBe('done');
    expect(rowsOf(state)[0]?.created).toBe(1);
    // Text that arrives after the stop does not reopen the message.
    expect(firstOf(state, 'assistant').text).toBe('Working');
    expect(firstOf(state, 'assistant').streaming).toBe(false);
    expect(state.turnOpen).toBe(false);
  });

  it('does not merge the footers of different turns', () => {
    const state = transcriptFromTimeline(timeline(user('one'), turnFinished(), user('two'), turnStopped, turnFinished('stopped')));
    const footers = only(state, 'turn');
    expect(footers).toHaveLength(2);
    expect(footers[0]?.stopped).toBe(false);
    expect(footers[1]?.stopped).toBe(true);
    expect(kinds(state)).toEqual(['user', 'turn', 'user', 'turn']);
  });

  it('settles a message that starts after the stop when the turn finally finishes', () => {
    const state = transcriptFromTimeline(timeline(user('x'), turnStopped, delta('m9', 'Late'), turnFinished('stopped')));
    expect(kinds(state)).toEqual(['user', 'assistant', 'turn']);
    expect(firstOf(state, 'assistant').streaming).toBe(false);
  });

  it("treats a 'stopped' stop reason as stopped", () => {
    const state = transcriptFromTimeline(timeline(user('x'), turnFinished('stopped')));
    expect(firstOf(state, 'turn').stopped).toBe(true);
  });

  it('gives each turn its own footer', () => {
    const state = transcriptFromTimeline(timeline(user('one'), turnFinished(), user('two'), turnFinished('max_tokens')));
    const footers = only(state, 'turn');
    expect(footers).toHaveLength(2);
    expect(footers[1]?.stopReason).toBe('max_tokens');
    expect(footers[0]?.key).not.toBe(footers[1]?.key);
  });

  it('keeps answered questions and resolved approvals untouched when the turn ends', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('x'),
        asked('r1'),
        { type: 'question.answered', requestId: 'r1', answers: [{ questionId: 'q-door', answer: 'East wall' }] },
        approvalRequested('a1'),
        { type: 'approval.resolved', requestId: 'a1', decision: 'reject' },
        started('c1'),
        finished('c1'),
        turnFinished(),
      ),
    );
    expect(firstOf(state, 'question').status).toBe('answered');
    expect(firstOf(state, 'approval').decision).toBe('reject');
    expect(firstOf(state, 'approval').cancelled).toBe(false);
    expect(rowsOf(state)[0]?.status).toBe('done');
  });

  it('settles a turn that never reported its end when the next message arrives', () => {
    const state = transcriptFromTimeline(
      timeline(user('one'), delta('m1', 'Thinking'), started('c1'), plan(task('t1', 'in_progress')), user('two')),
    );
    expect(kinds(state)).toEqual(['user', 'assistant', 'activity', 'plan', 'user']);
    const message: AssistantMessageItem = firstOf(state, 'assistant');
    expect(message.streaming).toBe(false);
    expect(rowsOf(state)[0]?.status).toBe('stopped');
    expect(state.livePlan).toBeNull();
    expect(state.turnOpen).toBe(true);
  });
});

describe('transcript reducer: structural guarantees', () => {
  it('keeps the identity of items that did not change', () => {
    const base = transcriptFromTimeline(timeline(user('x'), done('m1', 'First'), started('c1'), finished('c1')));
    const next = applyEntry(base, entry(5, delta('m2', 'Second')));
    expect(next.items[0]).toBe(base.items[0]);
    expect(next.items[1]).toBe(base.items[1]);
    expect(next.items[2]).toBe(base.items[2]);
    const streamed = applyEntry(next, entry(6, delta('m2', ' more')));
    expect(streamed.items[2]).toBe(base.items[2]);
    expect(streamed.items[3]).not.toBe(next.items[3]);
  });

  it('never mutates the previous state', () => {
    const base = transcriptFromTimeline(timeline(user('x'), started('c1')));
    const frozenItems = base.items.slice();
    const frozenRows = rowsOf(base).map((row) => ({ ...row }));
    applyEntries(base, [entry(3, finished('c1', result({ createdIds: ['a'] }))), entry(4, started('c2')), entry(5, turnStopped)]);
    expect(base.items).toEqual(frozenItems);
    expect(rowsOf(base)).toEqual(frozenRows);
    expect(base.lastSeq).toBe(2);
  });

  it('gives every item a unique key', () => {
    const state = transcriptFromTimeline(
      timeline(
        user('one'),
        delta('m1', 'a'),
        done('m1', 'a'),
        started('c1'),
        finished('c1'),
        asked('r1'),
        approvalRequested('a1'),
        plan(task('t', 'done')),
        { type: 'error', message: 'oops', retryable: false },
        turnFinished(),
        user('two'),
        delta('m2', 'b'),
        started('c2'),
        plan(task('t', 'done')),
        turnStopped,
      ),
    );
    const keys = state.items.map((item) => item.key);
    expect(new Set(keys).size).toBe(keys.length);
  });

  it('knows when the transcript itself shows progress', () => {
    expect(hasVisibleProgress(INITIAL_TRANSCRIPT)).toBe(false);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'))))).toBe(false);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'), delta('m1', 'Hi'))))).toBe(true);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'), done('m1', 'Hi'))))).toBe(false);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'), started('c1'))))).toBe(true);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'), started('c1'), finished('c1'))))).toBe(false);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'), asked('r1'))))).toBe(true);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'), approvalRequested('a1'))))).toBe(true);
    expect(hasVisibleProgress(transcriptFromTimeline(timeline(user('x'), delta('m1', 'Hi'), turnStopped)))).toBe(false);
  });

  it('replays a full conversation identically from a snapshot or entry by entry', () => {
    const entries = timeline(
      user('Draw a 12 x 14 ft room'),
      delta('m1', 'I will '),
      delta('m1', 'draw it.'),
      done('m1', 'I will draw it.'),
      asked('r1'),
      { type: 'question.answered', requestId: 'r1', answers: [{ questionId: 'q-thickness', answer: '9 in' }] },
      plan(task('t1', 'in_progress'), task('t2', 'pending')),
      started('c1'),
      finished('c1', result({ createdIds: ['w1', 'w2', 'w3', 'w4'] })),
      plan(task('t1', 'done'), task('t2', 'in_progress')),
      approvalRequested('a1'),
      { type: 'approval.resolved', requestId: 'a1', decision: 'approve-all' },
      started('c2'),
      finished('c2', result({ success: false, errors: ['Layer is locked'] })),
      { type: 'error', message: 'The model is overloaded', retryable: true },
      turnFinished('error'),
    );
    const whole = transcriptFromTimeline(entries);
    let stepped = INITIAL_TRANSCRIPT;
    for (const item of entries) stepped = applyEntry(stepped, item);
    expect(stepped).toEqual(whole);
    expect(kinds(whole)).toEqual(['user', 'assistant', 'question', 'activity', 'approval', 'activity', 'error', 'plan', 'turn']);
    expect(whole.lastSeq).toBe(entries.length);
  });
});
