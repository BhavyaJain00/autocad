import { beforeEach, describe, expect, it } from 'vitest';
import type { Attachment, CadToolExecutor, QuestionAnswer, SessionEvent } from '@cadstudio/copilot-agent';
import type { PickedAttachment, TimelineEntry } from '../shared/bridge';
import { BUSY_MESSAGE, EMPTY_MESSAGE, SessionHost, type SessionLike } from './session-host';
import { TimelineRecorder } from './timeline';

const USAGE = { inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheCreationTokens: 0 };
const finished = (stopReason = 'end_turn'): SessionEvent => ({
  type: 'turn.finished',
  stopReason,
  usage: USAGE,
  model: 'claude-opus-5-5',
});

/** A session driven by the test: `emit` sends events, `finishTurn` settles the running send(). */
class FakeSession implements SessionLike {
  readonly calls: [string, ...unknown[]][] = [];
  readonly sent: { text: string; attachments: Attachment[] | undefined }[] = [];
  #listeners = new Set<(event: SessionEvent) => void>();
  #settle: (() => void) | null = null;
  #fail: ((error: Error) => void) | null = null;
  throwOn: string | null = null;

  #call(name: string, ...args: unknown[]): void {
    this.calls.push([name, ...args]);
    if (this.throwOn === name) throw new Error(`${name} exploded`);
  }
  on(listener: (event: SessionEvent) => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }
  send(text: string, attachments?: Attachment[]): Promise<void> {
    this.sent.push({ text, attachments });
    if (this.throwOn === 'send') throw new Error('send exploded');
    return new Promise<void>((resolve, reject) => {
      this.#settle = resolve;
      this.#fail = reject;
    });
  }
  answerQuestion(requestId: string, answers: QuestionAnswer[]): void {
    this.#call('answerQuestion', requestId, answers);
  }
  resolveApproval(requestId: string, decision: 'approve' | 'reject' | 'approve-all'): void {
    this.#call('resolveApproval', requestId, decision);
  }
  stop(): void {
    this.#call('stop');
  }
  setApiKey(apiKey: string | null): void {
    this.#call('setApiKey', apiKey);
  }
  setModel(model: string): void {
    this.#call('setModel', model);
  }
  setEffort(effort: string): void {
    this.#call('setEffort', effort);
  }
  setApprovalMode(mode: string): void {
    this.#call('setApprovalMode', mode);
  }
  setExecutor(executor: CadToolExecutor | null): void {
    this.#call('setExecutor', executor);
  }
  reset(): void {
    this.#call('reset');
  }

  emit(event: SessionEvent): void {
    for (const listener of [...this.#listeners]) listener(event);
  }
  async finishTurn(): Promise<void> {
    this.#settle?.();
    this.#settle = null;
    await flush();
  }
  async failTurn(error: Error): Promise<void> {
    this.#fail?.(error);
    this.#fail = null;
    await flush();
  }
}

async function flush(): Promise<void> {
  for (let index = 0; index < 5; index += 1) await Promise.resolve();
}

interface Harness {
  host: SessionHost;
  session: FakeSession;
  entries: TimelineEntry[];
  busy: boolean[];
  logs: string[];
}

function createHarness(createSession?: () => SessionLike): Harness {
  const session = new FakeSession();
  const entries: TimelineEntry[] = [];
  const busy: boolean[] = [];
  const logs: string[] = [];
  let tick = 0;
  const host = new SessionHost({
    createSession: createSession ?? (() => session),
    onTimeline: (entry) => entries.push(entry),
    onBusy: (value) => busy.push(value),
    log: (message) => logs.push(message),
    recorder: new TimelineRecorder({ now: () => new Date(Date.UTC(2026, 0, 1, 0, 0, tick++)) }),
  });
  host.start();
  return { host, session, entries, busy, logs };
}

const types = (entries: TimelineEntry[]): string[] => entries.map((entry) => entry.type);

const ATTACHMENT: PickedAttachment = { name: 'plan.png', mediaType: 'image/png', dataBase64: 'AAAA', sizeBytes: 3 };

describe('SessionHost', () => {
  let h: Harness;

  beforeEach(() => {
    h = createHarness();
  });

  describe('sendMessage', () => {
    it('records the user message, turns busy and starts the turn without waiting for it', () => {
      expect(h.host.sendMessage('draw a room', [ATTACHMENT])).toEqual({ ok: true });
      expect(h.entries).toEqual([
        {
          type: 'user.message',
          text: 'draw a room',
          attachments: [{ name: 'plan.png', mediaType: 'image/png', sizeBytes: 3 }],
          seq: 1,
          at: '2026-01-01T00:00:00.000Z',
        },
      ]);
      expect(h.busy).toEqual([true]);
      expect(h.host.busy).toBe(true);
      // The session receives the content; the timeline never does.
      expect(h.session.sent).toEqual([
        { text: 'draw a room', attachments: [{ name: 'plan.png', mediaType: 'image/png', dataBase64: 'AAAA' }] },
      ]);
      expect(JSON.stringify(h.entries)).not.toContain('AAAA');
    });

    it('clears busy when the turn settles', async () => {
      h.host.sendMessage('hello', []);
      h.session.emit(finished());
      expect(h.host.busy).toBe(true);
      await h.session.finishTurn();
      expect(h.busy).toEqual([true, false]);
      expect(h.host.busy).toBe(false);
    });

    it('refuses a second message while a turn is running', () => {
      h.host.sendMessage('first', []);
      expect(h.host.sendMessage('second', [])).toEqual({ ok: false, message: BUSY_MESSAGE });
      expect(h.session.sent).toHaveLength(1);
      expect(types(h.entries)).toEqual(['user.message']);
    });

    it('accepts the next message after the turn finished', async () => {
      h.host.sendMessage('first', []);
      await h.session.finishTurn();
      expect(h.host.sendMessage('second', [])).toEqual({ ok: true });
      expect(h.busy).toEqual([true, false, true]);
    });

    it('refuses an empty message without attachments but accepts attachments alone', () => {
      expect(h.host.sendMessage('   ', [])).toEqual({ ok: false, message: EMPTY_MESSAGE });
      expect(h.entries).toEqual([]);
      expect(h.busy).toEqual([]);
      expect(h.host.sendMessage('', [ATTACHMENT])).toEqual({ ok: true });
    });

    it('ends the turn visibly when send() rejects against its contract', async () => {
      h.host.sendMessage('hello', []);
      await h.session.failTurn(new Error('socket hang up'));
      expect(types(h.entries)).toEqual(['user.message', 'error']);
      expect(h.entries[1]).toMatchObject({ type: 'error', retryable: true });
      expect(h.busy).toEqual([true, false]);
    });

    it('ends the turn visibly when send() throws synchronously', async () => {
      h.session.throwOn = 'send';
      expect(h.host.sendMessage('hello', [])).toEqual({ ok: true });
      await flush();
      expect(types(h.entries)).toEqual(['user.message', 'error']);
      expect(h.host.busy).toBe(false);
    });
  });

  describe('session events', () => {
    it('forwards every event as a stamped timeline entry, in order', async () => {
      h.host.sendMessage('go', []);
      const events: SessionEvent[] = [
        { type: 'message.delta', messageId: 'm1', text: 'Dra' },
        { type: 'message.delta', messageId: 'm1', text: 'wing' },
        { type: 'message.done', messageId: 'm1', text: 'Drawing' },
        { type: 'plan.updated', tasks: [{ id: 't1', title: 'Walls', status: 'in_progress' }] },
        { type: 'tool.started', callId: 'c1', name: 'create_line', title: 'Create line', summary: 'A line', args: {} },
        { type: 'tool.finished', callId: 'c1', name: 'create_line', result: null, error: 'connection lost' },
        { type: 'error', message: 'Overloaded', retryable: true },
        finished(),
      ];
      for (const event of events) h.session.emit(event);

      expect(h.entries.slice(1).map(({ seq: _seq, at: _at, ...event }) => event)).toEqual(events);
      expect(h.entries.map((entry) => entry.seq)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9]);
    });

    it('snapshots coalesce the streaming text while live entries carry each delta', () => {
      h.host.sendMessage('go', []);
      h.session.emit({ type: 'message.delta', messageId: 'm1', text: 'Hel' });
      h.session.emit({ type: 'message.delta', messageId: 'm1', text: 'lo' });
      expect(h.entries.slice(1)).toMatchObject([{ text: 'Hel' }, { text: 'lo' }]);
      expect(h.host.timeline()).toMatchObject([{ type: 'user.message' }, { type: 'message.delta', text: 'Hello', seq: 3 }]);

      h.session.emit({ type: 'message.done', messageId: 'm1', text: 'Hello' });
      expect(types(h.host.timeline())).toEqual(['user.message', 'message.done']);
    });

    it('records events that arrive outside a turn too', () => {
      h.session.emit({ type: 'error', message: 'No API key', retryable: false });
      expect(types(h.host.timeline())).toEqual(['error']);
    });
  });

  describe('questions', () => {
    const ask = (requestId: string): SessionEvent => ({
      type: 'question.asked',
      requestId,
      questions: [{ id: 'wall', header: 'WALL', question: 'Thickness?', options: [{ label: '200 mm' }], allowCustom: true }],
    });

    it('records the answer and passes it to the session', () => {
      h.host.sendMessage('go', []);
      h.session.emit(ask('q1'));
      h.host.answerQuestion('q1', [{ questionId: 'wall', answer: '200 mm' }]);
      expect(types(h.entries)).toEqual(['user.message', 'question.asked', 'question.answered']);
      expect(h.entries[2]).toMatchObject({ requestId: 'q1', answers: [{ questionId: 'wall', answer: '200 mm' }] });
      expect(h.session.calls).toEqual([['answerQuestion', 'q1', [{ questionId: 'wall', answer: '200 mm' }]]]);
    });

    it('ignores an answer to a question that is not pending', () => {
      h.host.sendMessage('go', []);
      h.host.answerQuestion('q-unknown', []);
      h.session.emit(ask('q1'));
      h.host.answerQuestion('q1', []);
      h.host.answerQuestion('q1', []);
      expect(types(h.entries).filter((type) => type === 'question.answered')).toHaveLength(1);
      expect(h.session.calls).toHaveLength(1);
    });

    it('a question is no longer pending once the turn finished or was stopped', async () => {
      h.host.sendMessage('go', []);
      h.session.emit(ask('q1'));
      h.host.stop();
      h.host.answerQuestion('q1', []);
      h.session.emit(ask('q2'));
      h.session.emit(finished('stopped'));
      h.host.answerQuestion('q2', []);
      expect(types(h.entries)).not.toContain('question.answered');
      expect(h.session.calls.map(([name]) => name)).toEqual(['stop']);
    });
  });

  describe('approvals', () => {
    const request = (requestId: string): SessionEvent => ({
      type: 'approval.requested',
      requestId,
      callId: 'c1',
      name: 'delete_entities',
      title: 'Delete',
      summary: 'Delete 3 objects',
      risk: 'destructive',
    });

    it('records the decision and passes it to the session', () => {
      h.host.sendMessage('go', []);
      h.session.emit(request('p1'));
      h.host.resolveApproval('p1', 'approve-all');
      expect(h.entries.at(-1)).toMatchObject({ type: 'approval.resolved', requestId: 'p1', decision: 'approve-all' });
      expect(h.session.calls).toEqual([['resolveApproval', 'p1', 'approve-all']]);
    });

    it('ignores decisions for approvals that are not pending', () => {
      h.host.sendMessage('go', []);
      h.host.resolveApproval('p1', 'approve');
      h.session.emit(request('p1'));
      h.host.resolveApproval('p1', 'reject');
      h.host.resolveApproval('p1', 'approve');
      expect(types(h.entries).filter((type) => type === 'approval.resolved')).toHaveLength(1);
      expect(h.session.calls).toEqual([['resolveApproval', 'p1', 'reject']]);
    });
  });

  describe('stop', () => {
    it('records turn.stopped and stops the session while a turn is running', async () => {
      h.host.sendMessage('go', []);
      h.host.stop();
      expect(types(h.entries)).toEqual(['user.message', 'turn.stopped']);
      expect(h.session.calls).toEqual([['stop']]);
      h.session.emit(finished('stopped'));
      await h.session.finishTurn();
      expect(types(h.entries)).toEqual(['user.message', 'turn.stopped', 'turn.finished']);
      expect(h.host.busy).toBe(false);
    });

    it('does nothing when no turn is running', () => {
      h.host.stop();
      expect(h.entries).toEqual([]);
      expect(h.session.calls).toEqual([]);
    });
  });

  describe('reset', () => {
    it('resets the session and restarts the timeline with a session.reset entry', async () => {
      h.host.sendMessage('one', []);
      h.session.emit(finished());
      await h.session.finishTurn();

      h.host.reset();
      expect(h.session.calls).toEqual([['reset']]);
      expect(h.entries.at(-1)).toMatchObject({ type: 'session.reset', seq: 3 });
      expect(h.host.timeline()).toEqual([h.entries.at(-1)]);

      h.host.sendMessage('two', []);
      expect(types(h.host.timeline())).toEqual(['session.reset', 'user.message']);
      expect(h.host.timeline().at(-1)?.seq).toBe(4);
    });

    it('drops what a running turn still emits and stays busy until it has wound down', async () => {
      h.host.sendMessage('one', []);
      h.session.emit({ type: 'message.delta', messageId: 'm1', text: 'Thinking' });
      h.host.reset();

      // Late events of the stopped turn.
      h.session.emit({ type: 'message.delta', messageId: 'm1', text: ' more' });
      h.session.emit(finished('stopped'));
      expect(types(h.host.timeline())).toEqual(['session.reset']);
      expect(h.host.busy).toBe(true);
      expect(h.host.sendMessage('two', [])).toEqual({ ok: false, message: BUSY_MESSAGE });
      // Stop has nothing to stop any more.
      h.host.stop();
      expect(types(h.host.timeline())).toEqual(['session.reset']);

      await h.session.finishTurn();
      expect(h.host.busy).toBe(false);
      expect(h.busy).toEqual([true, false]);

      expect(h.host.sendMessage('two', [])).toEqual({ ok: true });
      h.session.emit({ type: 'message.delta', messageId: 'm2', text: 'Fresh' });
      expect(types(h.host.timeline())).toEqual(['session.reset', 'user.message', 'message.delta']);
    });

    it('forgets pending questions and approvals', () => {
      h.host.sendMessage('one', []);
      h.session.emit({ type: 'question.asked', requestId: 'q1', questions: [] });
      h.host.reset();
      h.host.answerQuestion('q1', []);
      expect(types(h.host.timeline())).toEqual(['session.reset']);
    });
  });

  describe('settings, key and executor', () => {
    it('applies only the settings that are given', () => {
      h.host.applySettings({ model: 'claude-sonnet-5-5' });
      h.host.applySettings({ effort: 'low', approvalMode: 'auto' });
      h.host.applySettings({});
      expect(h.session.calls).toEqual([
        ['setModel', 'claude-sonnet-5-5'],
        ['setEffort', 'low'],
        ['setApprovalMode', 'auto'],
      ]);
    });

    it('passes the API key and the executor on', () => {
      const executor: CadToolExecutor = {
        cadName: () => 'CAD Studio',
        supportedTools: () => [],
        currentDocument: () => null,
        execute: () => Promise.reject(new Error('unused')),
      };
      h.host.setApiKey('sk-ant-key');
      h.host.setApiKey(null);
      h.host.setExecutor(executor);
      h.host.setExecutor(null);
      expect(h.session.calls).toEqual([
        ['setApiKey', 'sk-ant-key'],
        ['setApiKey', null],
        ['setExecutor', executor],
        ['setExecutor', null],
      ]);
    });

    it('survives a session method that throws', () => {
      h.session.throwOn = 'setModel';
      expect(() => h.host.applySettings({ model: 'claude-haiku-5-5' })).not.toThrow();
      expect(h.logs.some((line) => line.includes('setModel'))).toBe(true);
    });

    it('dispose stops a running turn without recording anything', () => {
      h.host.dispose();
      expect(h.session.calls).toEqual([]);
      h.host.sendMessage('go', []);
      h.host.dispose();
      expect(h.session.calls).toEqual([['stop']]);
      expect(types(h.entries)).toEqual(['user.message']);
    });
  });

  describe('a session that cannot be created', () => {
    it('keeps the host usable and refuses messages with the reason', () => {
      const broken = createHarness(() => {
        throw new Error('not implemented: CopilotSession.on');
      });
      expect(broken.host.busy).toBe(false);
      expect(broken.host.timeline()).toEqual([]);
      const result = broken.host.sendMessage('hello', []);
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.message).toContain('not implemented: CopilotSession.on');
      expect(() => {
        broken.host.stop();
        broken.host.setExecutor(null);
        broken.host.applySettings({ effort: 'low' });
        broken.host.answerQuestion('q', []);
      }).not.toThrow();
      // Reset still restarts the timeline.
      broken.host.reset();
      expect(types(broken.host.timeline())).toEqual(['session.reset']);
    });
  });

  it('a failing timeline or busy listener does not break the host', () => {
    const session = new FakeSession();
    const host = new SessionHost({
      createSession: () => session,
      onTimeline: () => {
        throw new Error('renderer gone');
      },
      onBusy: () => {
        throw new Error('renderer gone');
      },
    });
    host.start();
    expect(host.sendMessage('hello', [])).toEqual({ ok: true });
    expect(host.busy).toBe(true);
    expect(host.timeline()).toHaveLength(1);
  });
});
