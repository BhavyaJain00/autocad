import { describe, expect, it } from 'vitest';
import type { TimelineEntry } from '../shared/bridge';
import { TimelineRecorder, type TimelineEvent } from './timeline';

/** A recorder whose clock advances one second per entry from a fixed start. */
function createRecorder(maxEntries?: number): TimelineRecorder {
  let tick = 0;
  return new TimelineRecorder({
    now: () => new Date(Date.UTC(2026, 0, 1, 0, 0, tick++)),
    ...(maxEntries !== undefined ? { maxEntries } : {}),
  });
}

const delta = (messageId: string, text: string): TimelineEvent => ({ type: 'message.delta', messageId, text });
const done = (messageId: string, text: string): TimelineEvent => ({ type: 'message.done', messageId, text });
const userMessage = (text: string): TimelineEvent => ({ type: 'user.message', text, attachments: [] });
const turnFinished: TimelineEvent = {
  type: 'turn.finished',
  stopReason: 'end_turn',
  usage: { inputTokens: 1, outputTokens: 2, cacheReadTokens: 0, cacheCreationTokens: 0 },
  model: 'claude-opus-5-5',
};

const types = (entries: TimelineEntry[]): string[] => entries.map((entry) => entry.type);

describe('TimelineRecorder', () => {
  it('stamps entries with increasing sequence numbers and ISO timestamps', () => {
    const recorder = createRecorder();
    const first = recorder.record(userMessage('hello'));
    const second = recorder.record({ type: 'turn.stopped' });
    expect(first).toEqual({
      type: 'user.message',
      text: 'hello',
      attachments: [],
      seq: 1,
      at: '2026-01-01T00:00:00.000Z',
    });
    expect(second).toEqual({ type: 'turn.stopped', seq: 2, at: '2026-01-01T00:00:01.000Z' });
    expect(recorder.lastSeq).toBe(2);
    expect(recorder.snapshot()).toEqual([first, second]);
  });

  it('starts empty', () => {
    const recorder = createRecorder();
    expect(recorder.snapshot()).toEqual([]);
    expect(recorder.lastSeq).toBe(0);
  });

  it('returns every delta live but keeps one coalesced entry for an unfinished message', () => {
    const recorder = createRecorder();
    recorder.record(userMessage('draw a room'));
    const live = [recorder.record(delta('m1', 'I will ')), recorder.record(delta('m1', 'draw ')), recorder.record(delta('m1', 'it.'))];

    expect(live.map((entry) => (entry.type === 'message.delta' ? entry.text : ''))).toEqual(['I will ', 'draw ', 'it.']);
    expect(live.map((entry) => entry.seq)).toEqual([2, 3, 4]);

    const snapshot = recorder.snapshot();
    expect(types(snapshot)).toEqual(['user.message', 'message.delta']);
    expect(snapshot[1]).toEqual({
      type: 'message.delta',
      messageId: 'm1',
      text: 'I will draw it.',
      // The coalesced entry carries the stamp of the latest delta merged into it.
      seq: 4,
      at: '2026-01-01T00:00:03.000Z',
    });
  });

  it('replaces the coalesced entry in place when the message completes', () => {
    const recorder = createRecorder();
    recorder.record(userMessage('hi'));
    recorder.record(delta('m1', 'Hel'));
    recorder.record({ type: 'plan.updated', tasks: [{ id: 't1', title: 'Greet', status: 'in_progress' }] });
    recorder.record(delta('m1', 'lo'));
    const finished = recorder.record(done('m1', 'Hello'));

    const snapshot = recorder.snapshot();
    expect(types(snapshot)).toEqual(['user.message', 'message.done', 'plan.updated']);
    expect(snapshot[1]).toEqual(finished);
    expect(finished.seq).toBe(5);
  });

  it('keeps messages apart by messageId', () => {
    const recorder = createRecorder();
    recorder.record(delta('m1', 'A'));
    recorder.record(delta('m2', 'X'));
    recorder.record(delta('m1', 'B'));
    recorder.record(done('m2', 'XY'));
    const snapshot = recorder.snapshot();
    expect(snapshot).toHaveLength(2);
    expect(snapshot[0]).toMatchObject({ type: 'message.delta', messageId: 'm1', text: 'AB' });
    expect(snapshot[1]).toMatchObject({ type: 'message.done', messageId: 'm2', text: 'XY' });
  });

  it('records a message.done that had no deltas', () => {
    const recorder = createRecorder();
    recorder.record(done('m1', 'Done.'));
    expect(recorder.snapshot()).toEqual([
      { type: 'message.done', messageId: 'm1', text: 'Done.', seq: 1, at: '2026-01-01T00:00:00.000Z' },
    ]);
  });

  it('starts a new coalesced entry when a finished message id streams again', () => {
    const recorder = createRecorder();
    recorder.record(delta('m1', 'one'));
    recorder.record(done('m1', 'one'));
    recorder.record(delta('m1', 'two'));
    expect(types(recorder.snapshot())).toEqual(['message.done', 'message.delta']);
    expect(recorder.snapshot()[1]).toMatchObject({ text: 'two' });
  });

  it('leaves an unfinished message as it was when the turn ends', () => {
    const recorder = createRecorder();
    recorder.record(delta('m1', 'Partial answ'));
    recorder.record({ type: 'turn.stopped' });
    recorder.record({ ...turnFinished, stopReason: 'stopped' } as TimelineEvent);
    const snapshot = recorder.snapshot();
    expect(types(snapshot)).toEqual(['message.delta', 'turn.stopped', 'turn.finished']);
    expect(snapshot[0]).toMatchObject({ text: 'Partial answ' });
  });

  it('records every other entry type unchanged and in order', () => {
    const recorder = createRecorder();
    const events: TimelineEvent[] = [
      userMessage('go'),
      { type: 'tool.started', callId: 'c1', name: 'create_line', title: 'Create line', summary: 'Line', args: { a: 1 } },
      {
        type: 'tool.finished',
        callId: 'c1',
        name: 'create_line',
        result: { success: true, createdIds: ['e1'], modifiedIds: [], deletedIds: [], warnings: [], errors: [], revision: 2 },
      },
      { type: 'question.asked', requestId: 'q1', questions: [] },
      { type: 'question.answered', requestId: 'q1', answers: [{ questionId: 'a', answer: '200' }] },
      { type: 'approval.requested', requestId: 'p1', callId: 'c2', name: 'delete_entities', title: 'Delete', summary: 'x', risk: 'destructive' },
      { type: 'approval.resolved', requestId: 'p1', decision: 'approve' },
      { type: 'error', message: 'Overloaded', retryable: true },
      turnFinished,
    ];
    for (const event of events) recorder.record(event);
    const snapshot = recorder.snapshot();
    expect(snapshot.map(({ seq: _seq, at: _at, ...event }) => event)).toEqual(events);
    expect(snapshot.map((entry) => entry.seq)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9]);
  });

  it('snapshots are copies: changing one does not change the recorder', () => {
    const recorder = createRecorder();
    recorder.record(delta('m1', 'abc'));
    const snapshot = recorder.snapshot();
    (snapshot[0] as { text: string }).text = 'changed';
    snapshot.push({ type: 'turn.stopped', seq: 99, at: 'x' });
    expect(recorder.snapshot()).toHaveLength(1);
    expect(recorder.snapshot()[0]).toMatchObject({ text: 'abc' });
  });

  it('the live entry of a first delta is not the recorded object', () => {
    const recorder = createRecorder();
    const live = recorder.record(delta('m1', 'a'));
    recorder.record(delta('m1', 'b'));
    expect(live).toMatchObject({ text: 'a', seq: 1 });
  });

  it('reset forgets the conversation, keeps counting and starts with a session.reset entry', () => {
    const recorder = createRecorder();
    recorder.record(userMessage('one'));
    recorder.record(delta('m1', 'streaming'));
    const reset = recorder.reset();
    expect(reset).toEqual({ type: 'session.reset', seq: 3, at: '2026-01-01T00:00:02.000Z' });
    expect(recorder.snapshot()).toEqual([reset]);

    // A delta of the forgotten message does not resurrect its old text.
    recorder.record(delta('m1', 'new'));
    expect(recorder.snapshot()[1]).toMatchObject({ type: 'message.delta', text: 'new', seq: 4 });
  });

  it('drops the oldest entries beyond the limit', () => {
    const recorder = createRecorder(3);
    recorder.record(delta('m1', 'old'));
    recorder.record(userMessage('1'));
    recorder.record(userMessage('2'));
    recorder.record(userMessage('3'));
    const snapshot = recorder.snapshot();
    expect(snapshot.map((entry) => entry.seq)).toEqual([2, 3, 4]);

    // The dropped streaming message starts over instead of writing into a forgotten entry.
    recorder.record(delta('m1', 'later'));
    const after = recorder.snapshot();
    expect(after).toHaveLength(3);
    expect(after.at(-1)).toMatchObject({ type: 'message.delta', text: 'later' });
  });

  it('falls back to the default limit for a nonsensical one', () => {
    const recorder = new TimelineRecorder({ maxEntries: 0 });
    recorder.record(userMessage('a'));
    recorder.record(userMessage('b'));
    expect(recorder.snapshot()).toHaveLength(2);
  });
});
