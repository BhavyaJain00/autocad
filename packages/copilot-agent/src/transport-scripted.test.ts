import { describe, it, expect } from 'vitest';
import { SYSTEM_PROMPT } from './prompt';
import { CopilotSession } from './session';
import { FAKE_SOURCE, fakeExecutor, historyProblems } from './test-support';
import { SCRIPT_FINISHED_TEXT, createScriptedTransport, parseModelScript } from './transport-scripted';
import type { ModelRequest, SessionEvent } from './types';

const REQUEST: ModelRequest = {
  model: 'claude-opus-5-5',
  effort: 'high',
  system: SYSTEM_PROMPT,
  tools: [],
  messages: [{ role: 'user', content: 'Hi' }],
};

function hooks(signal: AbortSignal = new AbortController().signal) {
  const deltas: string[] = [];
  return { deltas, value: { signal, onTextDelta: (text: string) => void deltas.push(text) } };
}

describe('createScriptedTransport', () => {
  it('replays the turns in order: streamed text, then tool calls with generated ids', async () => {
    const transport = createScriptedTransport({
      turns: [
        { text: 'I will draw the outer walls of the room first.', toolCalls: [{ name: 'draw_line', input: { x1: 0 } }, { name: 'get_info' }] },
        { text: 'All done.' },
      ],
    });
    expect(transport.kind).toBe('scripted');

    const first = hooks();
    const one = await transport.run(REQUEST, first.value);
    expect(first.deltas.length).toBeGreaterThan(1);
    expect(first.deltas.length).toBeLessThanOrEqual(4);
    expect(first.deltas.join('')).toBe('I will draw the outer walls of the room first.');
    expect(one.stopReason).toBe('tool_use');
    expect(one.model).toBe('scripted');
    expect(one.usage).toEqual({ inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 });
    expect(one.content).toEqual([
      { type: 'text', text: 'I will draw the outer walls of the room first.', citations: null },
      { type: 'tool_use', id: 'toolu_scripted_0001', name: 'draw_line', input: { x1: 0 } },
      { type: 'tool_use', id: 'toolu_scripted_0002', name: 'get_info', input: {} },
    ]);

    const second = hooks();
    const two = await transport.run(REQUEST, second.value);
    expect(two.stopReason).toBe('end_turn');
    expect(second.deltas.join('')).toBe('All done.');
    expect(two.content).toEqual([{ type: 'text', text: 'All done.', citations: null }]);
  });

  it('answers "Script finished." with end_turn once the script is used up', async () => {
    const transport = createScriptedTransport({ turns: [{ text: 'Only turn.' }] });
    await transport.run(REQUEST, hooks().value);
    for (let index = 0; index < 2; index += 1) {
      const after = hooks();
      const response = await transport.run(REQUEST, after.value);
      expect(response.stopReason).toBe('end_turn');
      expect(after.deltas.join('')).toBe(SCRIPT_FINISHED_TEXT);
      expect(response.content).toEqual([{ type: 'text', text: 'Script finished.', citations: null }]);
    }
    const empty = createScriptedTransport({ turns: [] });
    expect((await empty.run(REQUEST, hooks().value)).content).toEqual([{ type: 'text', text: 'Script finished.', citations: null }]);
  });

  it('honours an explicit stop reason and tool-only turns', async () => {
    const transport = createScriptedTransport({
      turns: [{ text: 'Cut', stopReason: 'max_tokens' }, { toolCalls: [{ name: 'get_info', input: {} }] }, { stopReason: 'refusal' }],
    });
    expect((await transport.run(REQUEST, hooks().value)).stopReason).toBe('max_tokens');
    const toolOnly = hooks();
    const response = await transport.run(REQUEST, toolOnly.value);
    expect(toolOnly.deltas).toEqual([]);
    expect(response.content.map((block) => block.type)).toEqual(['tool_use']);
    expect(response.stopReason).toBe('tool_use');
    const refusal = await transport.run(REQUEST, hooks().value);
    expect(refusal.stopReason).toBe('refusal');
    expect(refusal.content).toEqual([]);
  });

  it('waits for delayMs', async () => {
    const transport = createScriptedTransport({ turns: [{ text: 'Slow answer here.', delayMs: 60 }] });
    const started = Date.now();
    await transport.run(REQUEST, hooks().value);
    expect(Date.now() - started).toBeGreaterThanOrEqual(45);
  });

  it('rejects promptly when aborted during a delay and stops streaming', async () => {
    const transport = createScriptedTransport({ turns: [{ text: 'This answer takes a long time to arrive.', delayMs: 20_000 }] });
    const controller = new AbortController();
    const h = hooks(controller.signal);
    const started = Date.now();
    const run = transport.run(REQUEST, h.value);
    setTimeout(() => controller.abort(), 10);
    await expect(run).rejects.toMatchObject({ name: 'AbortError' });
    expect(Date.now() - started).toBeLessThan(2000);
    expect(h.deltas.join('').length).toBeLessThan('This answer takes a long time to arrive.'.length);
  });

  it('rejects immediately when the signal is already aborted', async () => {
    const transport = createScriptedTransport({ turns: [{ text: 'Never.' }] });
    const controller = new AbortController();
    controller.abort();
    const h = hooks(controller.signal);
    await expect(transport.run(REQUEST, h.value)).rejects.toMatchObject({ name: 'AbortError' });
    expect(h.deltas).toEqual([]);
  });

  it('accepts the script as JSON text', async () => {
    const transport = createScriptedTransport(JSON.stringify({ turns: [{ text: 'From JSON.' }] }));
    const h = hooks();
    await transport.run(REQUEST, h.value);
    expect(h.deltas.join('')).toBe('From JSON.');
  });

  it('validates the script', () => {
    expect(() => createScriptedTransport(null)).toThrow(/Invalid model script/);
    expect(() => createScriptedTransport({})).toThrow(/turns/);
    expect(() => createScriptedTransport({ turns: 'x' })).toThrow(/turns/);
    expect(() => createScriptedTransport({ turns: [{ text: 5 }] })).toThrow(/turns\[0\]\.text/);
    expect(() => createScriptedTransport({ turns: [{ toolCalls: [{ input: {} }] }] })).toThrow(/turns\[0\]\.toolCalls\[0\]\.name/);
    expect(() => createScriptedTransport({ turns: [{ delayMs: -1 }] })).toThrow(/delayMs/);
    expect(() => createScriptedTransport({ turns: [{ text: 'x', delay: 5 }] })).toThrow(/"delay"/);
    expect(() => createScriptedTransport('{not json')).toThrow(/not valid JSON/);
    expect(parseModelScript({ turns: [{ text: 'ok', delayMs: 0 }] }).turns).toHaveLength(1);
  });

  it('drives a complete session turn without any network access', async () => {
    const cad = fakeExecutor();
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-opus-5-5',
      effort: 'high',
      approvalMode: 'auto',
      executor: cad,
      toolSource: FAKE_SOURCE,
      transport: createScriptedTransport({
        turns: [
          { text: 'Drawing a line.', toolCalls: [{ name: 'draw_line', input: { x1: 0, y1: 0, x2: 10, y2: 0 } }] },
          { text: 'The line is in place.' },
        ],
      }),
    });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Draw a line');

    const types = events.map((event) => event.type);
    expect(types.filter((type) => type !== 'message.delta')).toEqual(['message.done', 'tool.started', 'tool.finished', 'message.done', 'turn.finished']);
    expect(events.at(-1)).toMatchObject({ stopReason: 'end_turn', model: 'scripted' });
    expect(cad.calls).toHaveLength(1);
    expect(historyProblems(session.historySnapshot())).toEqual([]);

    await session.send('And now?');
    expect(events.at(-2)).toMatchObject({ type: 'message.done', text: 'Script finished.' });
  });

  it('can be stopped by the session in the middle of a scripted delay', async () => {
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-opus-5-5',
      effort: 'high',
      approvalMode: 'auto',
      executor: null,
      transport: createScriptedTransport({ turns: [{ text: 'A very slow scripted answer.', delayMs: 30_000 }, { text: 'Next.' }] }),
    });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    const turn = session.send('Go');
    setTimeout(() => session.stop(), 10);
    await turn;
    expect(events.at(-1)).toMatchObject({ type: 'turn.finished', stopReason: 'stopped' });
    await session.send('Again');
    expect(events.at(-1)).toMatchObject({ type: 'turn.finished', stopReason: 'end_turn' });
  });
});
