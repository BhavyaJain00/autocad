import { describe, it, expect } from 'vitest';
import {
  APIConnectionError,
  AuthenticationError,
  NotFoundError,
  OverloadedError,
  RateLimitError,
} from '@anthropic-ai/sdk';
import type { BetaContentBlock } from '@anthropic-ai/sdk/resources/beta/messages/messages';
import { SYSTEM_PROMPT } from './prompt';
import { CopilotSession, echoableContent, requiresApproval, withoutThinking } from './session';
import {
  FAKE_SOURCE,
  blocksOf,
  fakeExecutor,
  fakeTransport,
  hangUntilAborted,
  harness,
  historyProblems,
  okResult,
  reply,
  resultIsError,
  resultTextFor,
  textsOf,
  toolResultsOf,
} from './test-support';
import { CANCELLED_MESSAGE, DECLINED_MESSAGE, MAX_TOOL_RESULT_CHARS } from './tool-result';
import { SCRIPTED_MODEL_NAME, createScriptedTransport } from './transport-scripted';
import type { ApprovalMode, ModelRunHooks, SessionEvent } from './types';

const LINE = { x1: 0, y1: 0, x2: 100, y2: 0 };
const headers = new Headers();

describe('CopilotSession: text-only turn', () => {
  it('streams the text, records the exchange and reports usage', async () => {
    const h = harness([reply({ text: 'Hello there.', usage: { inputTokens: 100, outputTokens: 20, cacheReadTokens: 7 } })]);
    await h.session.send('Hi');

    expect(h.types()).toEqual(['message.delta', 'message.delta', 'message.done', 'turn.finished']);
    const deltas = h.of('message.delta');
    const done = h.of('message.done')[0];
    expect(deltas.map((event) => event.text).join('')).toBe('Hello there.');
    expect(done?.text).toBe('Hello there.');
    expect(new Set(deltas.map((event) => event.messageId))).toEqual(new Set([done?.messageId]));
    expect(h.of('turn.finished')[0]).toEqual({
      type: 'turn.finished',
      stopReason: 'end_turn',
      usage: { inputTokens: 100, outputTokens: 20, cacheReadTokens: 7, cacheCreationTokens: 0 },
      model: 'fake-model',
    });
    expect(h.session.busy).toBe(false);

    const history = h.session.historySnapshot();
    expect(history.map((message) => message.role)).toEqual(['user', 'assistant']);
    expect(historyProblems(history)).toEqual([]);
  });

  it('sends the stable system prompt, the tool list and the user text followed by the CAD context', async () => {
    const h = harness([reply({ text: 'ok' })]);
    await h.session.send('  Draw a line  ');

    const request = h.transport.requests[0];
    expect(request?.system).toBe(SYSTEM_PROMPT);
    expect(request?.model).toBe('claude-opus-5-5');
    expect(request?.effort).toBe('high');
    // Catalog order (not the order the connector listed), then the two local tools.
    expect(request?.tools.map((tool) => tool.name)).toEqual(['get_info', 'draw_line', 'delete_things', 'ask_user', 'update_plan']);
    const texts = textsOf(request?.messages[0]);
    expect(texts).toHaveLength(2);
    expect(texts[0]).toBe('Draw a line');
    expect(texts[1]).toContain('<cad_context>');
    expect(texts[1]).toContain('Connected CAD: CAD Studio');
    expect(texts[1]).toContain('Drawing: "Plan.cadstudio" | unit: mm | revision: 7 | entities: 42');
  });

  it('offers only the local tools and says so when no CAD is connected', async () => {
    const h = harness([reply({ text: 'ok' })], { cad: null });
    await h.session.send('Hi');
    const request = h.transport.requests[0];
    expect(request?.tools.map((tool) => tool.name)).toEqual(['ask_user', 'update_plan']);
    expect(textsOf(request?.messages[0])[1]).toContain('No CAD application is connected');
  });

  it('says when the CAD has no drawing open', async () => {
    const h = harness([reply({ text: 'ok' })], { cad: fakeExecutor({ document: null }) });
    await h.session.send('Hi');
    expect(textsOf(h.transport.requests[0]?.messages[0])[1]).toContain('No drawing is open.');
  });

  it('emits text that the transport did not stream as one whole message', async () => {
    const h = harness([], {
      transport: { kind: 'silent', run: async () => reply({ text: 'Not streamed.' }) },
    });
    await h.session.send('Hi');
    expect(h.types()).toEqual(['message.delta', 'message.done', 'turn.finished']);
    expect(h.of('message.done')[0]?.text).toBe('Not streamed.');
  });

  it('keeps thinking blocks in the history exactly as they were produced', async () => {
    const first = reply({ thinking: true, text: 'Answer.' });
    const h = harness([first, reply({ text: 'Again.' })]);
    await h.session.send('One');
    await h.session.send('Two');
    const assistant = h.transport.requests[1]?.messages[1];
    expect(blocksOf(assistant)).toHaveLength(2);
    expect(blocksOf(assistant)[0]).toBe(first.content[0]);
    expect(blocksOf(assistant)[1]).toBe(first.content[1]);
  });
});

describe('CopilotSession: tool calls', () => {
  it('runs a tool round trip with events in order', async () => {
    const h = harness([
      reply({ text: 'Drawing it.', tools: [{ id: 'call-1', name: 'draw_line', input: LINE }] }),
      reply({ text: 'Done: one line.' }),
    ]);
    await h.session.send('Draw a line');

    expect(h.types()).toEqual([
      'message.delta',
      'message.delta',
      'message.done',
      'tool.started',
      'tool.finished',
      'message.delta',
      'message.delta',
      'message.done',
      'turn.finished',
    ]);
    const started = h.of('tool.started')[0];
    expect(started).toMatchObject({ callId: 'call-1', name: 'draw_line', title: 'Draw line', summary: 'Draw line' });
    // The arguments are the validated ones (defaults applied).
    expect(started?.args).toEqual({ ...LINE, layer: '0' });
    const finished = h.of('tool.finished')[0];
    expect(finished?.callId).toBe('call-1');
    expect(finished?.result?.success).toBe(true);
    expect(finished?.error).toBeUndefined();

    const call = h.cad?.calls[0];
    expect(call?.name).toBe('draw_line');
    expect(call?.args).toEqual({ ...LINE, layer: '0' });
    expect(call?.opts.instruction).toBe('Draw a line');
    expect(call?.opts.operationId).toMatch(/^op_/);
    expect(call?.opts.signal).toBeInstanceOf(AbortSignal);

    const history = h.session.historySnapshot();
    expect(history.map((message) => message.role)).toEqual(['user', 'assistant', 'user', 'assistant']);
    expect(historyProblems(history)).toEqual([]);
    const result = JSON.parse(resultTextFor(history, 'call-1') ?? '{}') as { success: boolean; revision: number; createdIds: string[] };
    expect(result).toMatchObject({ success: true, revision: 8, createdIds: ['e1'] });
    expect(resultIsError(history, 'call-1')).toBe(false);
    // The second request carries the whole exchange so far.
    expect(h.transport.requests[1]?.messages).toHaveLength(3);
    expect(h.of('turn.finished')[0]?.usage.inputTokens).toBe(20);
  });

  it('returns all results of one assistant turn in a single user message, in order, run one at a time', async () => {
    const order: string[] = [];
    const cad = fakeExecutor({
      handlers: {
        draw_line: async (call, self) => {
          order.push(`start ${String((call.args as { x2: number }).x2)}`);
          await new Promise((resolve) => setTimeout(resolve, 5));
          order.push(`end ${String((call.args as { x2: number }).x2)}`);
          return okResult((self.document?.revision ?? 0) + self.calls.length);
        },
      },
    });
    const h = harness(
      [
        reply({
          tools: [
            { id: 'a', name: 'draw_line', input: { ...LINE, x2: 1 } },
            { id: 'b', name: 'get_info' },
            { id: 'c', name: 'draw_line', input: { ...LINE, x2: 3 } },
          ],
        }),
        reply({ text: 'All done.' }),
      ],
      { cad },
    );
    await h.session.send('Draw two lines');

    expect(order).toEqual(['start 1', 'end 1', 'start 3', 'end 3']);
    expect(cad.maxConcurrent).toBe(1);
    expect(cad.calls.map((call) => call.name)).toEqual(['draw_line', 'get_info', 'draw_line']);
    const history = h.session.historySnapshot();
    expect(history).toHaveLength(4);
    expect(toolResultsOf(history[2]).map((block) => block.tool_use_id)).toEqual(['a', 'b', 'c']);
    expect(historyProblems(history)).toEqual([]);
    expect(h.of('tool.started').map((event) => event.callId)).toEqual(['a', 'b', 'c']);
  });

  it('answers invalid arguments with the list of problems and lets the model correct itself', async () => {
    const h = harness([
      reply({ tools: [{ id: 'bad', name: 'draw_line', input: { x1: 0, y1: 'zero', extra: true } }] }),
      reply({ tools: [{ id: 'good', name: 'draw_line', input: LINE }] }),
      reply({ text: 'Fixed.' }),
    ]);
    await h.session.send('Draw');

    const history = h.session.historySnapshot();
    const text = resultTextFor(history, 'bad') ?? '';
    expect(resultIsError(history, 'bad')).toBe(true);
    expect(text).toContain('Invalid arguments for draw_line');
    expect(text).toContain('y1');
    expect(text).toContain('x2');
    expect(text).toContain('extra');
    expect(h.cad?.calls).toHaveLength(1);
    expect(resultIsError(history, 'good')).toBe(false);
    // Nothing reached the CAD for the invalid call, so there is no CAD activity for it.
    expect(h.of('tool.started').map((event) => event.callId)).toEqual(['good']);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('end_turn');
    expect(historyProblems(history)).toEqual([]);
  });

  it('answers unsupported and unknown tools with error results and continues', async () => {
    const h = harness([
      reply({
        tools: [
          { id: 'unsupported', name: 'draw_arc', input: { radius: 5 } },
          { id: 'unknown', name: 'run_script', input: { code: 'rm -rf' } },
          { id: 'fine', name: 'get_info' },
        ],
      }),
      reply({ text: 'ok' }),
    ]);
    await h.session.send('Go');

    const history = h.session.historySnapshot();
    expect(resultTextFor(history, 'unsupported')).toBe('CAD Studio does not support draw_arc. Use one of the listed tools instead.');
    expect(resultIsError(history, 'unsupported')).toBe(true);
    expect(resultTextFor(history, 'unknown')).toBe('Unknown tool "run_script". Only the listed tools exist.');
    expect(resultIsError(history, 'unknown')).toBe(true);
    expect(resultIsError(history, 'fine')).toBe(false);
    expect(h.cad?.calls.map((call) => call.name)).toEqual(['get_info']);
    expect(h.transport.requests).toHaveLength(2);
  });

  it('refuses CAD tools when no CAD is connected', async () => {
    const h = harness([reply({ tools: [{ id: 'x', name: 'draw_line', input: LINE }] }), reply({ text: 'ok' })], { cad: null });
    await h.session.send('Draw');
    const history = h.session.historySnapshot();
    expect(resultTextFor(history, 'x')).toContain('No CAD application is connected');
    expect(resultIsError(history, 'x')).toBe(true);
  });

  it('turns an executor rejection into an error result and continues', async () => {
    const cad = fakeExecutor({
      handlers: {
        draw_line: () => {
          throw new Error('pipe closed');
        },
      },
    });
    const h = harness([reply({ tools: [{ id: 'x', name: 'draw_line', input: LINE }] }), reply({ text: 'It failed.' })], { cad });
    await h.session.send('Draw');

    const finished = h.of('tool.finished')[0];
    expect(finished?.result).toBeNull();
    expect(finished?.error).toBe('pipe closed');
    const history = h.session.historySnapshot();
    expect(resultIsError(history, 'x')).toBe(true);
    expect(resultTextFor(history, 'x')).toContain('pipe closed');
    expect(h.of('turn.finished')[0]?.stopReason).toBe('end_turn');
    expect(h.of('error')).toHaveLength(0);
  });

  it('reports a failed ToolResult to the model as an error and to the UI as a result', async () => {
    const cad = fakeExecutor({
      handlers: {
        draw_line: () => ({ ...okResult(7), success: false, errors: ['Layer "Walls" does not exist'] }),
      },
    });
    const h = harness([reply({ tools: [{ id: 'x', name: 'draw_line', input: LINE }] }), reply({ text: 'ok' })], { cad });
    await h.session.send('Draw');
    const history = h.session.historySnapshot();
    expect(resultIsError(history, 'x')).toBe(true);
    expect(resultTextFor(history, 'x')).toContain('Layer \\"Walls\\" does not exist');
    expect(h.of('tool.finished')[0]?.result?.success).toBe(false);
  });

  it('rejects a malformed result from the CAD', async () => {
    const cad = fakeExecutor({ handlers: { get_info: () => ({ ok: true }) as never } });
    const h = harness([reply({ tools: [{ id: 'x', name: 'get_info' }] }), reply({ text: 'ok' })], { cad });
    await h.session.send('Look');
    expect(h.of('tool.finished')[0]).toMatchObject({ result: null, error: 'The CAD returned a result that could not be read.' });
    expect(resultIsError(h.session.historySnapshot(), 'x')).toBe(true);
  });

  it('caps a huge tool result and tells the model what was cut', async () => {
    const entities = Array.from({ length: 5000 }, (_, index) => ({ id: `entity-${index}`, type: 'line', layer: 'Walls' }));
    const cad = fakeExecutor({ handlers: { get_info: () => okResult(7, { data: { entities } }) } });
    const h = harness([reply({ tools: [{ id: 'x', name: 'get_info' }] }), reply({ text: 'ok' })], { cad });
    await h.session.send('Look');
    const text = resultTextFor(h.session.historySnapshot(), 'x') ?? '';
    expect(text.length).toBeLessThanOrEqual(MAX_TOOL_RESULT_CHARS);
    const parsed = JSON.parse(text) as { success: boolean; data: { entities: unknown[] }; truncated: { lists: string[] } };
    expect(parsed.success).toBe(true);
    expect(parsed.data.entities.length).toBeGreaterThan(100);
    expect(parsed.data.entities.length).toBeLessThan(5000);
    expect(parsed.truncated.lists[0]).toBe(`data.entities: ${parsed.data.entities.length} of 5000`);
  });
});

describe('CopilotSession: local tools', () => {
  it('asks the user, waits for the answers and returns them to the model', async () => {
    const h = harness([
      reply({
        text: 'One question first.',
        tools: [
          {
            id: 'ask',
            name: 'ask_user',
            input: {
              questions: [
                {
                  header: 'Wall thickness',
                  question: 'How thick should the walls be?',
                  options: [{ label: '230 mm', description: 'Standard brick', recommended: true }, { label: '115 mm' }],
                },
                {
                  header: 'Door',
                  question: 'Where does the door go?',
                  options: [{ label: 'North' }, { label: 'South' }],
                  allowCustom: false,
                },
              ],
            },
          },
        ],
      }),
      reply({ text: 'Thanks.' }),
    ]);
    const asked = h.next('question.asked');
    const turn = h.session.send('Draw a room');
    const event = await asked;

    expect(h.session.busy).toBe(true);
    expect(event.questions).toEqual([
      {
        id: 'q1',
        header: 'WALL THICKNESS',
        question: 'How thick should the walls be?',
        options: [{ label: '230 mm', description: 'Standard brick', recommended: true }, { label: '115 mm' }],
        allowCustom: true,
      },
      {
        id: 'q2',
        header: 'DOOR',
        question: 'Where does the door go?',
        options: [{ label: 'North' }, { label: 'South' }],
        allowCustom: false,
      },
    ]);
    // The model is not called again until the user has answered.
    expect(h.transport.requests).toHaveLength(1);
    h.session.answerQuestion('not-the-request', [{ questionId: 'q1', answer: 'ignored' }]);
    expect(h.transport.requests).toHaveLength(1);

    h.session.answerQuestion(event.requestId, [
      { questionId: 'q2', answer: ' South ' },
      { questionId: 'q1', answer: '200 mm (custom)' },
    ]);
    await turn;

    const history = h.session.historySnapshot();
    expect(JSON.parse(resultTextFor(history, 'ask') ?? '{}')).toEqual({
      answers: [
        { question: 'How thick should the walls be?', answer: '200 mm (custom)' },
        { question: 'Where does the door go?', answer: 'South' },
      ],
    });
    expect(resultIsError(history, 'ask')).toBe(false);
    expect(h.of('tool.started')).toHaveLength(0);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('end_turn');
  });

  it('marks unanswered questions instead of inventing answers', async () => {
    const h = harness([
      reply({
        tools: [
          {
            id: 'ask',
            name: 'ask_user',
            input: {
              questions: [
                { header: 'A', question: 'First?', options: [{ label: 'x' }, { label: 'y' }] },
                { header: 'B', question: 'Second?', options: [{ label: 'x' }, { label: 'y' }] },
              ],
            },
          },
        ],
      }),
      reply({ text: 'ok' }),
    ]);
    const asked = h.next('question.asked');
    const turn = h.session.send('Go');
    const event = await asked;
    h.session.answerQuestion(event.requestId, [{ questionId: 'q1', answer: 'x' }, { questionId: 'q9', answer: 'bogus' }]);
    await turn;
    const parsed = JSON.parse(resultTextFor(h.session.historySnapshot(), 'ask') ?? '{}') as { answers: unknown[]; note?: string };
    expect(parsed.answers).toEqual([
      { question: 'First?', answer: 'x' },
      { question: 'Second?', answer: null },
    ]);
    expect(parsed.note).toContain('unanswered');
  });

  it('rejects malformed questions without bothering the user', async () => {
    const h = harness([
      reply({
        tools: [{ id: 'ask', name: 'ask_user', input: { questions: [{ header: 'A', question: 'Only one option?', options: [{ label: 'x' }] }] } }],
      }),
      reply({ text: 'ok' }),
    ]);
    await h.session.send('Go');
    expect(h.of('question.asked')).toHaveLength(0);
    const history = h.session.historySnapshot();
    expect(resultIsError(history, 'ask')).toBe(true);
    expect(resultTextFor(history, 'ask')).toContain('Invalid arguments for ask_user');
    expect(resultTextFor(history, 'ask')).toContain('questions[0].options');
  });

  it('publishes the plan and answers ok', async () => {
    const tasks = [
      { id: 'walls', title: 'Draw the walls', status: 'in_progress' },
      { id: 'doors', title: 'Add the doors', status: 'pending' },
    ];
    const h = harness([
      reply({ tools: [{ id: 'plan', name: 'update_plan', input: { tasks } }] }),
      reply({ tools: [{ id: 'plan2', name: 'update_plan', input: { tasks: [{ ...tasks[0], status: 'done' }, { ...tasks[1], status: 'failed' }] } }] }),
      reply({ text: 'ok' }),
    ]);
    await h.session.send('Plan it');
    const plans = h.of('plan.updated');
    expect(plans).toHaveLength(2);
    expect(plans[0]?.tasks).toEqual(tasks);
    expect(plans[1]?.tasks.map((task) => task.status)).toEqual(['done', 'failed']);
    const history = h.session.historySnapshot();
    expect(resultTextFor(history, 'plan')).toBe('{"ok":true}');
    expect(resultIsError(history, 'plan')).toBe(false);
    expect(h.cad?.calls).toHaveLength(0);
  });

  it('rejects a plan with duplicate ids or an unknown status', async () => {
    const h = harness([
      reply({
        tools: [
          { id: 'dup', name: 'update_plan', input: { tasks: [{ id: 'a', title: 'One', status: 'pending' }, { id: 'a', title: 'Two', status: 'pending' }] } },
          { id: 'status', name: 'update_plan', input: { tasks: [{ id: 'a', title: 'One', status: 'started' }] } },
        ],
      }),
      reply({ text: 'ok' }),
    ]);
    await h.session.send('Plan it');
    expect(h.of('plan.updated')).toHaveLength(0);
    const history = h.session.historySnapshot();
    expect(resultTextFor(history, 'dup')).toContain('unique id');
    expect(resultIsError(history, 'dup')).toBe(true);
    expect(resultTextFor(history, 'status')).toContain('tasks[0].status');
  });
});

describe('CopilotSession: approvals', () => {
  const cases: { mode: ApprovalMode; tool: string; input: unknown; asks: boolean }[] = [
    { mode: 'ask-all', tool: 'get_info', input: {}, asks: false },
    { mode: 'ask-all', tool: 'draw_line', input: LINE, asks: true },
    { mode: 'ask-all', tool: 'delete_things', input: { ids: ['e1'] }, asks: true },
    { mode: 'ask-destructive', tool: 'get_info', input: {}, asks: false },
    { mode: 'ask-destructive', tool: 'draw_line', input: LINE, asks: false },
    { mode: 'ask-destructive', tool: 'delete_things', input: { ids: ['e1'] }, asks: true },
    { mode: 'auto', tool: 'get_info', input: {}, asks: false },
    { mode: 'auto', tool: 'draw_line', input: LINE, asks: false },
    { mode: 'auto', tool: 'delete_things', input: { ids: ['e1'] }, asks: true },
  ];

  for (const testCase of cases) {
    it(`${testCase.mode}: ${testCase.tool} ${testCase.asks ? 'asks' : 'runs without asking'}`, async () => {
      const h = harness([reply({ tools: [{ id: 'call', name: testCase.tool, input: testCase.input }] }), reply({ text: 'ok' })], {
        approvalMode: testCase.mode,
      });
      h.session.on((event) => {
        if (event.type === 'approval.requested') h.session.resolveApproval(event.requestId, 'approve');
      });
      await h.session.send('Go');

      const requests = h.of('approval.requested');
      expect(requests).toHaveLength(testCase.asks ? 1 : 0);
      if (testCase.asks) {
        expect(requests[0]).toMatchObject({
          callId: 'call',
          name: testCase.tool,
          risk: testCase.tool === 'delete_things' ? 'destructive' : 'write',
        });
        // The approval comes before any CAD activity.
        expect(h.types().indexOf('approval.requested')).toBeLessThan(h.types().indexOf('tool.started'));
      }
      expect(h.cad?.calls).toHaveLength(1);
      expect(resultIsError(h.session.historySnapshot(), 'call')).toBe(false);
    });
  }

  it('agrees with requiresApproval for every mode and risk', () => {
    expect(requiresApproval('read', 'ask-all')).toBe(false);
    expect(requiresApproval('write', 'ask-all')).toBe(true);
    expect(requiresApproval('write', 'ask-destructive')).toBe(false);
    expect(requiresApproval('write', 'auto')).toBe(false);
    expect(requiresApproval('destructive', 'auto')).toBe(true);
    expect(requiresApproval('destructive', 'ask-destructive')).toBe(true);
  });

  it('waits for the decision before touching the CAD', async () => {
    const h = harness([reply({ tools: [{ id: 'del', name: 'delete_things', input: { ids: ['e1', 'e2'] } }] }), reply({ text: 'Deleted.' })]);
    const requested = h.next('approval.requested');
    const turn = h.session.send('Delete them');
    const request = await requested;
    expect(request).toMatchObject({ callId: 'del', name: 'delete_things', title: 'Delete things', risk: 'destructive' });
    expect(h.cad?.calls).toHaveLength(0);
    expect(h.of('tool.started')).toHaveLength(0);
    h.session.resolveApproval('wrong-id', 'approve');
    expect(h.cad?.calls).toHaveLength(0);
    h.session.resolveApproval(request.requestId, 'approve');
    await turn;
    expect(h.cad?.calls).toHaveLength(1);
    expect(h.session.approvalMode).toBe('ask-destructive');
  });

  it('reject: the tool does not run and the model is told the user declined', async () => {
    const h = harness([reply({ tools: [{ id: 'del', name: 'delete_things', input: { ids: ['e1'] } }] }), reply({ text: 'Left as is.' })]);
    h.session.on((event) => {
      if (event.type === 'approval.requested') h.session.resolveApproval(event.requestId, 'reject');
    });
    await h.session.send('Delete it');

    expect(h.cad?.calls).toHaveLength(0);
    expect(h.of('tool.started')).toHaveLength(0);
    const history = h.session.historySnapshot();
    expect(resultTextFor(history, 'del')).toBe(DECLINED_MESSAGE);
    expect(DECLINED_MESSAGE).toBe('The user declined this action.');
    expect(resultIsError(history, 'del')).toBe(true);
    expect(h.transport.requests).toHaveLength(2);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('end_turn');
  });

  it('approve-all: switches the session to auto, so later writes run without asking', async () => {
    const h = harness(
      [
        reply({
          tools: [
            { id: 'one', name: 'draw_line', input: LINE },
            { id: 'two', name: 'draw_line', input: LINE },
            { id: 'del', name: 'delete_things', input: { ids: ['e1'] } },
          ],
        }),
        reply({ text: 'ok' }),
      ],
      { approvalMode: 'ask-all' },
    );
    const decisions: string[] = [];
    h.session.on((event) => {
      if (event.type !== 'approval.requested') return;
      decisions.push(event.callId);
      h.session.resolveApproval(event.requestId, event.callId === 'one' ? 'approve-all' : 'approve');
    });
    await h.session.send('Go');

    // The first write asked; the second did not; the destructive call still asked.
    expect(decisions).toEqual(['one', 'del']);
    expect(h.session.approvalMode).toBe('auto');
    expect(h.cad?.calls).toHaveLength(3);
  });

  it('honours a mode change made while the turn is running', async () => {
    const h = harness([reply({ tools: [{ id: 'one', name: 'draw_line', input: LINE }] }), reply({ tools: [{ id: 'two', name: 'draw_line', input: LINE }] }), reply({ text: 'ok' })]);
    h.session.on((event) => {
      if (event.type === 'tool.finished' && event.callId === 'one') h.session.setApprovalMode('ask-all');
      if (event.type === 'approval.requested') h.session.resolveApproval(event.requestId, 'approve');
    });
    await h.session.send('Go');
    expect(h.of('approval.requested').map((event) => event.callId)).toEqual(['two']);
  });
});

describe('CopilotSession: drawing revision', () => {
  it('sends the last seen revision with writes only, and updates it from every result', async () => {
    const h = harness([
      reply({ tools: [{ id: 'r', name: 'get_info' }, { id: 'w1', name: 'draw_line', input: LINE }, { id: 'w2', name: 'draw_line', input: LINE }] }),
      reply({ tools: [{ id: 'd', name: 'delete_things', input: { ids: ['e2'] } }] }),
      reply({ text: 'ok' }),
    ]);
    h.session.on((event) => {
      if (event.type === 'approval.requested') h.session.resolveApproval(event.requestId, 'approve');
    });
    await h.session.send('Go');

    const [read, write1, write2, destructive] = h.cad?.calls ?? [];
    expect(read?.opts).not.toHaveProperty('expectedRevision');
    expect(write1?.opts.expectedRevision).toBe(7);
    expect(write2?.opts.expectedRevision).toBe(8);
    expect(destructive?.opts.expectedRevision).toBe(9);
  });

  it('keeps the stale revision when the user edited the drawing between turns, until the model looks again', async () => {
    const h = harness([
      reply({ tools: [{ id: 'w1', name: 'draw_line', input: LINE }] }),
      reply({ text: 'Drawn.' }),
      reply({ tools: [{ id: 'w2', name: 'draw_line', input: LINE }] }),
      reply({ tools: [{ id: 'r', name: 'get_info' }, { id: 'w3', name: 'draw_line', input: LINE }] }),
      reply({ text: 'ok' }),
    ]);
    const cad = h.cad!;
    cad.handlers.draw_line = (call, self) => {
      const current = self.document?.revision ?? 0;
      if (call.opts.expectedRevision !== current) {
        return { ...okResult(current), success: false, errors: ['The drawing changed; inspect it again.'] };
      }
      self.document = { ...self.document!, revision: current + 1 };
      return okResult(current + 1);
    };
    await h.session.send('Draw');
    expect(cad.calls[0]?.opts.expectedRevision).toBe(7);

    // The user edits the drawing by hand: revision 8 -> 12.
    cad.document = { ...cad.document!, revision: 12, entityCount: 50 };
    await h.session.send('Draw another');

    const context = textsOf(h.transport.requests[2]?.messages[4]).at(-1) ?? '';
    expect(context).toContain('revision: 12');
    expect(context).toContain('edited since you last looked at it (you saw revision 8)');
    // The first write still carries the revision the model saw and is refused by the CAD ...
    expect(cad.calls[1]?.opts.expectedRevision).toBe(8);
    expect(resultIsError(h.session.historySnapshot(), 'w2')).toBe(true);
    // ... after the model inspected again, the write carries the current revision.
    expect(cad.calls[2]?.name).toBe('get_info');
    expect(cad.calls[3]?.opts.expectedRevision).toBe(12);
    expect(resultIsError(h.session.historySnapshot(), 'w3')).toBe(false);
  });

  it('tells the model when another drawing became active and starts from its revision', async () => {
    const h = harness([reply({ text: 'ok' }), reply({ tools: [{ id: 'w', name: 'draw_line', input: LINE }] }), reply({ text: 'ok' })]);
    await h.session.send('Hi');
    h.cad!.document = { id: 'doc-2', name: 'Other.cadstudio', path: null, unit: 'ft', revision: 3, entityCount: 0 };
    await h.session.send('Draw');
    const context = textsOf(h.transport.requests[1]?.messages[2]).at(-1) ?? '';
    expect(context).toContain('Drawing: "Other.cadstudio" | unit: ft | revision: 3 | entities: 0');
    expect(context).toContain('different drawing');
    expect(h.cad?.calls[0]?.opts.expectedRevision).toBe(3);
  });

  it('forgets the revision when another CAD is connected', async () => {
    const h = harness([reply({ tools: [{ id: 'w', name: 'draw_line', input: LINE }] }), reply({ text: 'ok' }), reply({ tools: [{ id: 'w2', name: 'draw_line', input: LINE }] }), reply({ text: 'ok' })]);
    await h.session.send('Draw');
    const other = fakeExecutor({ name: 'AutoCAD 2026', document: { id: 'dwg', name: 'A.dwg', path: null, unit: 'in', revision: 100, entityCount: 9 } });
    h.session.setExecutor(other);
    await h.session.send('Draw again');
    expect(other.calls[0]?.opts.expectedRevision).toBe(100);
    expect(textsOf(h.transport.requests[2]?.messages[4]).at(-1)).toContain('Connected CAD: AutoCAD 2026');
  });
});

describe('CopilotSession: stop()', () => {
  it('during streaming: closes the partial message, keeps the history valid and accepts a new message', async () => {
    let hooks: ModelRunHooks | undefined;
    const h = harness([
      hangUntilAborted((started) => {
        hooks = started;
        started.onTextDelta('Let me th', 0);
      }),
      reply({ text: 'Second answer.' }),
    ]);
    const delta = h.next('message.delta');
    const turn = h.session.send('First');
    await delta;
    expect(h.session.busy).toBe(true);
    h.session.stop();
    // Text that still arrives after the stop is not shown.
    hooks?.onTextDelta('ink', 0);
    await turn;

    expect(h.types()).toEqual(['message.delta', 'message.done', 'turn.finished']);
    expect(h.of('message.done')[0]?.text).toBe('Let me th');
    expect(h.of('turn.finished')[0]?.stopReason).toBe('stopped');
    expect(hooks?.signal.aborted).toBe(true);
    expect(h.session.busy).toBe(false);
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user']);
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);

    await h.session.send('Second');
    expect(h.of('turn.finished')[1]?.stopReason).toBe('end_turn');
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user', 'user', 'assistant']);
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
  });

  it('reports the scripted test model, not the configured Claude model, for a turn stopped before any response', async () => {
    const h = harness([], { transport: createScriptedTransport({ turns: [{ text: 'A long pause first.', delayMs: 10_000 }] }) });
    const turn = h.session.send('First');
    await Promise.resolve();
    h.session.stop();
    await turn;
    expect(h.of('turn.finished')[0]).toMatchObject({ stopReason: 'stopped', model: SCRIPTED_MODEL_NAME });
  });

  it('stops even when the transport ignores the abort signal', async () => {
    const h = harness([() => new Promise(() => undefined)]);
    const turn = h.session.send('First');
    await Promise.resolve();
    h.session.stop();
    await turn;
    expect(h.of('turn.finished')[0]?.stopReason).toBe('stopped');
  });

  it('during a question: cancels the question and every other pending tool call', async () => {
    const h = harness([
      reply({
        tools: [
          { id: 'ask', name: 'ask_user', input: { questions: [{ header: 'A', question: 'Which?', options: [{ label: 'x' }, { label: 'y' }] }] } },
          { id: 'after', name: 'draw_line', input: LINE },
        ],
      }),
      reply({ text: 'Later.' }),
    ]);
    const asked = h.next('question.asked');
    const turn = h.session.send('Go');
    const event = await asked;
    h.session.stop();
    await turn;

    expect(h.of('turn.finished')[0]?.stopReason).toBe('stopped');
    const history = h.session.historySnapshot();
    expect(historyProblems(history)).toEqual([]);
    expect(resultTextFor(history, 'ask')).toBe(CANCELLED_MESSAGE);
    expect(CANCELLED_MESSAGE).toBe('Cancelled by the user.');
    expect(resultTextFor(history, 'after')).toBe(CANCELLED_MESSAGE);
    expect(resultIsError(history, 'ask')).toBe(true);
    expect(resultIsError(history, 'after')).toBe(true);
    expect(h.cad?.calls).toHaveLength(0);
    expect(h.transport.requests).toHaveLength(1);
    // A late answer to the cancelled question changes nothing.
    h.session.answerQuestion(event.requestId, [{ questionId: 'q1', answer: 'x' }]);

    await h.session.send('Again');
    expect(h.of('turn.finished')[1]?.stopReason).toBe('end_turn');
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user', 'assistant', 'user', 'user', 'assistant']);
  });

  it('during an approval: nothing runs and the history stays valid', async () => {
    const h = harness([
      reply({ tools: [{ id: 'del', name: 'delete_things', input: { ids: ['e1'] } }, { id: 'info', name: 'get_info' }] }),
      reply({ text: 'Later.' }),
    ]);
    const requested = h.next('approval.requested');
    const turn = h.session.send('Delete');
    const request = await requested;
    h.session.stop();
    await turn;

    expect(h.of('turn.finished')[0]?.stopReason).toBe('stopped');
    expect(h.cad?.calls).toHaveLength(0);
    const history = h.session.historySnapshot();
    expect(historyProblems(history)).toEqual([]);
    expect(resultTextFor(history, 'del')).toBe(CANCELLED_MESSAGE);
    expect(resultTextFor(history, 'info')).toBe(CANCELLED_MESSAGE);
    h.session.resolveApproval(request.requestId, 'approve');
    expect(h.cad?.calls).toHaveLength(0);

    await h.session.send('Again');
    expect(h.of('turn.finished')[1]?.stopReason).toBe('end_turn');
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
  });

  it('during a tool call: aborts the call, answers every tool call and accepts a new message', async () => {
    let seenSignal: AbortSignal | undefined;
    const cad = fakeExecutor({
      handlers: {
        draw_line: (call) =>
          new Promise((_resolve, reject) => {
            seenSignal = call.opts.signal;
            call.opts.signal.addEventListener('abort', () => reject(new Error('cancelled by signal')));
          }),
      },
    });
    const h = harness(
      [
        reply({ tools: [{ id: 'slow', name: 'draw_line', input: LINE }, { id: 'next', name: 'get_info' }] }),
        reply({ text: 'Later.' }),
      ],
      { cad },
    );
    const started = h.next('tool.started');
    const turn = h.session.send('Draw');
    await started;
    h.session.stop();
    await turn;

    expect(seenSignal?.aborted).toBe(true);
    expect(h.types()).toEqual(['tool.started', 'tool.finished', 'turn.finished']);
    expect(h.of('tool.finished')[0]).toMatchObject({ callId: 'slow', result: null, error: 'Cancelled.' });
    expect(h.of('turn.finished')[0]?.stopReason).toBe('stopped');
    const history = h.session.historySnapshot();
    expect(historyProblems(history)).toEqual([]);
    expect(resultTextFor(history, 'slow')).toMatch(/^Cancelled by the user\./);
    expect(resultTextFor(history, 'slow')).toContain('may already have applied');
    expect(resultTextFor(history, 'next')).toBe(CANCELLED_MESSAGE);
    expect(cad.calls).toHaveLength(1);

    await h.session.send('Again');
    expect(h.of('turn.finished')[1]?.stopReason).toBe('end_turn');
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
  });

  it('lets send() follow stop() immediately', async () => {
    const h = harness([hangUntilAborted(), reply({ text: 'Second.' })]);
    const first = h.session.send('First');
    await Promise.resolve();
    h.session.stop();
    const second = h.session.send('Second');
    await Promise.all([first, second]);
    expect(h.of('turn.finished').map((event) => event.stopReason)).toEqual(['stopped', 'end_turn']);
    expect(h.of('error')).toHaveLength(0);
  });

  it('is a no-op when nothing is running', () => {
    const h = harness([]);
    h.session.stop();
    expect(h.events).toEqual([]);
    expect(h.session.busy).toBe(false);
  });
});

describe('CopilotSession: attachments', () => {
  const png = Buffer.from('fake-png').toString('base64');
  const pdf = Buffer.from('%PDF-1.7 fake').toString('base64');
  const notes = Buffer.from('Room A: 3 x 4 m\nRoom B: 2 x 2 m').toString('base64');

  it('puts attachments first, then the text, then the context block', async () => {
    const h = harness([reply({ text: 'ok' })]);
    await h.session.send('Use these', [
      { name: 'sketch.png', mediaType: 'image/png', dataBase64: png },
      { name: 'brief.pdf', mediaType: 'application/pdf', dataBase64: pdf },
      { name: 'rooms.txt', mediaType: 'text/plain', dataBase64: notes },
    ]);
    const blocks = blocksOf(h.transport.requests[0]?.messages[0]);
    expect(blocks.map((block) => block.type)).toEqual(['image', 'document', 'text', 'text', 'text']);
    expect(blocks[0]).toEqual({ type: 'image', source: { type: 'base64', media_type: 'image/png', data: png } });
    expect(blocks[1]).toEqual({
      type: 'document',
      source: { type: 'base64', media_type: 'application/pdf', data: pdf },
      title: 'brief.pdf',
    });
    expect(blocks[2]).toEqual({ type: 'text', text: 'Attached file "rooms.txt":\n\nRoom A: 3 x 4 m\nRoom B: 2 x 2 m' });
    expect(blocks[3]).toEqual({ type: 'text', text: 'Use these' });
    expect(textsOf(h.transport.requests[0]?.messages[0])[2]).toContain('<cad_context>');
  });

  it('accepts attachments without any text', async () => {
    const h = harness([reply({ text: 'ok' })]);
    await h.session.send('', [{ name: 'sketch.png', mediaType: 'image/png', dataBase64: png }]);
    expect(blocksOf(h.transport.requests[0]?.messages[0]).map((block) => block.type)).toEqual(['image', 'text']);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('end_turn');
  });

  it('does not start the turn for an unsupported attachment', async () => {
    const h = harness([reply({ text: 'ok' })]);
    await h.session.send('Use this', [{ name: 'plan.dwg', mediaType: 'application/acad', dataBase64: png }]);
    expect(h.types()).toEqual(['error', 'turn.finished']);
    const error = h.of('error')[0];
    expect(error?.message).toContain('"plan.dwg" (application/acad) is not a supported attachment');
    expect(error?.retryable).toBe(false);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('error');
    expect(h.transport.requests).toHaveLength(0);
    expect(h.session.historySnapshot()).toEqual([]);
  });

  it('does not start the turn for an empty message', async () => {
    const h = harness([reply({ text: 'ok' })]);
    await h.session.send('   ');
    expect(h.types()).toEqual(['error', 'turn.finished']);
    expect(h.transport.requests).toHaveLength(0);
    expect(h.session.historySnapshot()).toEqual([]);
  });
});

describe('CopilotSession: errors', () => {
  it('asks for an API key when there is none and no custom transport', async () => {
    const session = new CopilotSession({ apiKey: null, model: 'claude-opus-5-5', effort: 'high', approvalMode: 'auto', executor: null });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Hi');
    expect(events.map((event) => event.type)).toEqual(['error', 'turn.finished']);
    expect(events[0]).toEqual({
      type: 'error',
      message: 'Add your Anthropic API key in Settings to start using CAD Copilot.',
      retryable: false,
    });
    expect(events[1]).toMatchObject({ stopReason: 'error', model: 'claude-opus-5-5' });
    expect(session.historySnapshot()).toEqual([]);
    expect(session.busy).toBe(false);
  });

  it('treats a blank API key as missing', async () => {
    const session = new CopilotSession({ apiKey: '   ', model: 'claude-opus-5-5', effort: 'high', approvalMode: 'auto', executor: null });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Hi');
    expect(events[0]).toMatchObject({ type: 'error', retryable: false });
  });

  const failures: { name: string; error: () => unknown; text: string; retryable: boolean }[] = [
    { name: 'invalid API key', error: () => new AuthenticationError(401, {}, 'bad key', headers), text: 'API key is invalid', retryable: false },
    { name: 'rate limited', error: () => new RateLimitError(429, {}, 'slow down', headers), text: 'rate limiting', retryable: true },
    { name: 'overloaded', error: () => new OverloadedError(529, {}, 'busy', headers), text: 'overloaded', retryable: true },
    { name: 'offline', error: () => new APIConnectionError({ message: 'fetch failed' }), text: 'Could not reach the Anthropic API', retryable: true },
    { name: 'model unavailable', error: () => new NotFoundError(404, {}, 'model: x', headers), text: 'model is not available', retryable: false },
  ];

  for (const failure of failures) {
    it(`ends the turn with one plain error event when the API fails: ${failure.name}`, async () => {
      const h = harness([
        () => {
          throw failure.error();
        },
        reply({ text: 'Recovered.' }),
      ]);
      await h.session.send('Hi');
      expect(h.types()).toEqual(['error', 'turn.finished']);
      expect(h.of('error')[0]?.message).toContain(failure.text);
      expect(h.of('error')[0]?.retryable).toBe(failure.retryable);
      expect(h.of('turn.finished')[0]?.stopReason).toBe('error');
      expect(historyProblems(h.session.historySnapshot())).toEqual([]);

      // The user can simply send again.
      await h.session.send('Hi again');
      expect(h.of('turn.finished')[1]?.stopReason).toBe('end_turn');
      expect(h.of('message.done')[0]?.text).toBe('Recovered.');
    });
  }

  it('keeps the history valid when the API fails after tools have run', async () => {
    const h = harness([
      reply({ text: 'Working.', tools: [{ id: 'w', name: 'draw_line', input: LINE }] }),
      () => {
        throw new RateLimitError(429, {}, 'slow down', headers);
      },
      reply({ text: 'Finished.' }),
    ]);
    await h.session.send('Draw');
    expect(h.of('turn.finished')[0]?.stopReason).toBe('error');
    const history = h.session.historySnapshot();
    expect(history.map((message) => message.role)).toEqual(['user', 'assistant', 'user']);
    expect(historyProblems(history)).toEqual([]);

    await h.session.send('continue');
    expect(h.of('turn.finished')[1]?.stopReason).toBe('end_turn');
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
    // Usage of the failed turn still counts the call that succeeded.
    expect(h.of('turn.finished')[0]?.usage.inputTokens).toBe(10);
  });

  it('closes a partially streamed message when the stream fails', async () => {
    const h = harness([
      (_request, hooks) => {
        hooks.onTextDelta('Half an ans', 0);
        throw new OverloadedError(529, {}, 'busy', headers);
      },
    ]);
    await h.session.send('Hi');
    expect(h.types()).toEqual(['message.delta', 'message.done', 'error', 'turn.finished']);
    expect(h.of('message.done')[0]?.text).toBe('Half an ans');
  });

  it('reports a CAD connection that fails while the turn is prepared', async () => {
    const cad = fakeExecutor();
    cad.supportedTools = () => {
      throw new Error('not connected');
    };
    const h = harness([reply({ text: 'ok' })], { cad });
    await h.session.send('Hi');
    expect(h.types()).toEqual(['error', 'turn.finished']);
    expect(h.of('error')[0]?.message).toContain('The CAD connection is not responding: not connected');
    expect(h.transport.requests).toHaveLength(0);
  });

  it('rejects a second message while busy without disturbing the running turn', async () => {
    const h = harness([
      reply({ tools: [{ id: 'ask', name: 'ask_user', input: { questions: [{ header: 'A', question: 'Which?', options: [{ label: 'x' }, { label: 'y' }] }] } }] }),
      reply({ text: 'ok' }),
    ]);
    const asked = h.next('question.asked');
    const turn = h.session.send('First');
    const event = await asked;
    await h.session.send('Second');
    expect(h.of('error')).toEqual([
      { type: 'error', message: 'CAD Copilot is still working on the previous message. Wait for it to finish, or stop it first.', retryable: true },
    ]);
    expect(h.of('turn.finished')).toHaveLength(0);
    expect(h.session.busy).toBe(true);
    h.session.answerQuestion(event.requestId, [{ questionId: 'q1', answer: 'x' }]);
    await turn;
    expect(h.of('turn.finished')).toHaveLength(1);
    expect(h.session.historySnapshot().filter((message) => message.role === 'user' && textsOf(message).includes('Second'))).toHaveLength(0);
  });

  it('stops after the maximum number of model calls', async () => {
    const steps = Array.from({ length: 10 }, (_, index) => reply({ tools: [{ id: `loop-${index}`, name: 'get_info' }] }));
    const h = harness(steps, { maxModelCalls: 3 });
    await h.session.send('Loop');
    expect(h.transport.requests).toHaveLength(3);
    expect(h.cad?.calls).toHaveLength(3);
    expect(h.of('error')[0]?.message).toContain('Stopped after 3 model steps');
    expect(h.of('error')[0]?.retryable).toBe(true);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('error');
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
  });

  it('allows 80 model calls per turn by default', async () => {
    const steps = Array.from({ length: 90 }, (_, index) => reply({ tools: [{ id: `loop-${index}`, name: 'get_info' }] }));
    const h = harness(steps);
    await h.session.send('Loop');
    expect(h.transport.requests).toHaveLength(80);
    expect(h.of('error')[0]?.message).toContain('Stopped after 80 model steps');
  });

  it('survives listeners that throw', async () => {
    const h = harness([reply({ text: 'Hi.', tools: [{ id: 'w', name: 'draw_line', input: LINE }] }), reply({ text: 'Done.' })]);
    h.session.on(() => {
      throw new Error('listener bug');
    });
    const later: string[] = [];
    h.session.on((event) => later.push(event.type));
    await h.session.send('Draw');
    expect(h.of('turn.finished')[0]?.stopReason).toBe('end_turn');
    expect(later).toEqual(h.types());
    expect(h.logs.length).toBe(h.events.length);
    expect(h.logs[0]?.message).toContain('An event listener failed');
    expect((h.logs[0]?.error as Error).message).toBe('listener bug');
    expect(h.cad?.calls).toHaveLength(1);
  });

  it('stops notifying a listener after it unsubscribed', async () => {
    const h = harness([reply({ text: 'One.' }), reply({ text: 'Two.' })]);
    const seen: string[] = [];
    const off = h.session.on((event) => seen.push(event.type));
    await h.session.send('One');
    const count = seen.length;
    off();
    await h.session.send('Two');
    expect(count).toBeGreaterThan(0);
    expect(seen).toHaveLength(count);
  });
});

describe('CopilotSession: stop reasons', () => {
  it('refusal: reports it, records nothing of the declined reply and takes the request back', async () => {
    const h = harness([
      reply({ text: 'I cannot', stopReason: 'refusal', refusal: { category: 'cyber', explanation: 'Flagged by a classifier.' } }),
      reply({ text: 'Sure.' }),
    ]);
    await h.session.send('Something odd');
    expect(h.types()).toEqual(['message.delta', 'message.delta', 'message.done', 'error', 'turn.finished']);
    const error = h.of('error')[0];
    expect(error?.message).toContain('The model declined this request: its cybersecurity safeguards were triggered.');
    expect(error?.message).toContain('Flagged by a classifier.');
    expect(error?.retryable).toBe(false);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('refusal');
    expect(h.session.historySnapshot()).toEqual([]);

    await h.session.send('Rephrased');
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user', 'assistant']);
  });

  it('refusal without details still gives a clear message', async () => {
    const h = harness([reply({ stopReason: 'refusal' })]);
    await h.session.send('Hm');
    expect(h.of('error')[0]?.message).toBe('The model declined this request. Try rephrasing it, or choose another model in Settings.');
  });

  it('refusal after tools ran: never runs the tools of the declined reply and keeps the history valid', async () => {
    const h = harness([
      reply({ tools: [{ id: 'w', name: 'draw_line', input: LINE }] }),
      reply({ tools: [{ id: 'never', name: 'draw_line', input: LINE }], stopReason: 'refusal', refusal: { category: null, explanation: null } }),
    ]);
    await h.session.send('Draw');
    expect(h.cad?.calls).toHaveLength(1);
    const history = h.session.historySnapshot();
    expect(history.map((message) => message.role)).toEqual(['user', 'assistant', 'user']);
    expect(historyProblems(history)).toEqual([]);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('refusal');
  });

  it('max_tokens: does not run a possibly truncated tool call', async () => {
    const h = harness([
      reply({ text: 'Starting', tools: [{ id: 'cut', name: 'draw_line', input: LINE }], stopReason: 'max_tokens' }),
      reply({ text: 'Continuing.' }),
    ]);
    await h.session.send('Draw a lot');
    expect(h.cad?.calls).toHaveLength(0);
    expect(h.of('tool.started')).toHaveLength(0);
    expect(h.of('error')[0]?.message).toContain('output limit');
    expect(h.of('error')[0]?.retryable).toBe(true);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('max_tokens');
    const history = h.session.historySnapshot();
    expect(historyProblems(history)).toEqual([]);
    expect(resultIsError(history, 'cut')).toBe(true);
    expect(resultTextFor(history, 'cut')).toContain('cut off at the output limit');

    await h.session.send('continue');
    expect(h.of('turn.finished')[1]?.stopReason).toBe('end_turn');
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
  });

  it('max_tokens on plain text keeps the partial answer', async () => {
    const h = harness([reply({ text: 'A long answer that was cut', stopReason: 'max_tokens' })]);
    await h.session.send('Explain');
    expect(h.of('turn.finished')[0]?.stopReason).toBe('max_tokens');
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user', 'assistant']);
  });

  it('pause_turn: sends the paused turn back so the model can continue', async () => {
    const h = harness([reply({ text: 'Part one.', stopReason: 'pause_turn' }), reply({ text: 'Part two.' })]);
    await h.session.send('Go');
    expect(h.transport.requests).toHaveLength(2);
    expect(h.transport.requests[1]?.messages.map((message) => message.role)).toEqual(['user', 'assistant']);
    expect(h.of('turn.finished')[0]?.stopReason).toBe('end_turn');
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user', 'assistant', 'assistant']);
  });

  it('reads a missing stop reason as end_turn and reports unusual ones as they are', async () => {
    const h = harness([reply({ text: 'ok', stopReason: null }), reply({ text: 'ok', stopReason: 'model_context_window_exceeded' })]);
    await h.session.send('One');
    await h.session.send('Two');
    expect(h.of('turn.finished').map((event) => event.stopReason)).toEqual(['end_turn', 'model_context_window_exceeded']);
    expect(h.of('error')[0]?.message).toContain('too long for the model');
  });

  it('after a server-side fallback: drops the declined attempt, runs only the tool calls after the boundary', async () => {
    const content: BetaContentBlock[] = [
      { type: 'thinking', thinking: '', signature: 'sig-declined' },
      { type: 'text', text: 'Before.', citations: null },
      { type: 'tool_use', id: 'declined', name: 'draw_line', input: LINE },
      {
        type: 'fallback',
        from: { model: 'claude-opus-5-5' },
        to: { model: 'claude-opus-5' },
        trigger: { type: 'refusal' },
      } as BetaContentBlock,
      { type: 'thinking', thinking: '', signature: 'sig-fallback' },
      { type: 'text', text: 'After.', citations: null },
      { type: 'tool_use', id: 'kept', name: 'get_info', input: {} },
    ];
    const h = harness([
      { content, stopReason: 'tool_use', usage: { inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0 }, model: 'claude-opus-5' },
      reply({ text: 'ok' }),
    ]);
    await h.session.send('Go');

    const assistant = blocksOf(h.session.historySnapshot()[1]);
    expect(assistant.map((block) => block.type)).toEqual(['text', 'fallback', 'thinking', 'text', 'tool_use']);
    expect(assistant[1]).toBe(content[3]);
    expect(assistant[2]).toBe(content[4]);
    expect(h.cad?.calls.map((call) => call.name)).toEqual(['get_info']);
    expect(toolResultsOf(h.session.historySnapshot()[2]).map((block) => block.tool_use_id)).toEqual(['kept']);
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
    expect(h.of('turn.finished')[0]?.model).toBe('fake-model');
    expect(echoableContent(content.slice(0, 3))).toEqual(content.slice(0, 3));
  });

  it('never replays an empty text block (the API rejects those as input)', async () => {
    const empty: BetaContentBlock = { type: 'text', text: '', citations: null };
    const thinking: BetaContentBlock = { type: 'thinking', thinking: '', signature: 'sig' } as BetaContentBlock;
    const call: BetaContentBlock = { type: 'tool_use', id: 'x', name: 'get_info', input: {} };
    expect(echoableContent([thinking, empty, call])).toEqual([thinking, call]);
    expect(echoableContent([thinking, empty, call])[0]).toBe(thinking);

    const h = harness([
      { content: [empty], stopReason: 'end_turn', usage: { inputTokens: 1, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 }, model: 'fake-model' },
      reply({ text: 'Now with words.' }),
    ]);
    await h.session.send('One');
    expect(h.types()).toEqual(['turn.finished']);
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user']);
    await h.session.send('Two');
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
    expect(h.session.historySnapshot().map((message) => message.role)).toEqual(['user', 'user', 'assistant']);
  });
});

describe('CopilotSession: history and settings', () => {
  it('only ever appends to the history', async () => {
    const h = harness([
      reply({ thinking: true, text: 'Working.', tools: [{ id: 'w', name: 'draw_line', input: LINE }] }),
      reply({ thinking: true, text: 'Done.' }),
      reply({ thinking: true, text: 'Again.' }),
    ]);
    await h.session.send('One');
    const before = h.session.historySnapshot();
    const frozen = JSON.stringify(before);
    await h.session.send('Two');
    const after = h.session.historySnapshot();

    expect(after.length).toBe(before.length + 2);
    before.forEach((message, index) => expect(after[index]).toBe(message));
    expect(JSON.stringify(after.slice(0, before.length))).toBe(frozen);
    // Each request is the previous one plus the appended messages.
    const [first, second, third] = h.transport.requests;
    expect(second?.messages.slice(0, first?.messages.length)).toEqual(first?.messages);
    expect(third?.messages.slice(0, second?.messages.length)).toEqual(second?.messages);
    expect(third?.tools).toEqual(first?.tools);
    expect(third?.system).toBe(first?.system);
  });

  it('continues without thinking blocks when the tool list changes, and only then', async () => {
    const h = harness([
      reply({ thinking: true, text: 'One.', tools: [{ id: 'w', name: 'draw_line', input: LINE }] }),
      reply({ thinking: true, text: 'Done.' }),
      reply({ thinking: true, text: 'Two.' }),
      reply({ thinking: true, text: 'Three.' }),
    ]);
    await h.session.send('One');
    await h.session.send('Two');
    const types = (index: number): string[][] =>
      (h.transport.requests[index]?.messages ?? []).filter((message) => message.role === 'assistant').map((message) => blocksOf(message).map((block) => block.type));
    // Same CAD: thinking blocks are replayed.
    expect(types(2)).toEqual([['thinking', 'text', 'tool_use'], ['thinking', 'text']]);

    h.session.setExecutor(null);
    await h.session.send('Three');
    expect(h.transport.requests[3]?.tools.map((tool) => tool.name)).toEqual(['ask_user', 'update_plan']);
    expect(types(3)).toEqual([['text', 'tool_use'], ['text'], ['text']]);
    expect(historyProblems(h.session.historySnapshot())).toEqual([]);
    // New thinking is kept again from here on.
    expect(blocksOf(h.session.historySnapshot().at(-1)).map((block) => block.type)).toEqual(['thinking', 'text']);
  });

  it('withoutThinking drops reasoning-only messages and leaves the rest untouched', () => {
    const user = { role: 'user' as const, content: 'Hi' };
    const mixed = {
      role: 'assistant' as const,
      content: [
        { type: 'thinking' as const, thinking: '', signature: 's' },
        { type: 'redacted_thinking' as const, data: 'x' },
        { type: 'text' as const, text: 'Hello' },
      ],
    };
    const onlyThinking = { role: 'assistant' as const, content: [{ type: 'thinking' as const, thinking: '', signature: 's' }] };
    const plain = { role: 'assistant' as const, content: [{ type: 'text' as const, text: 'Plain' }] };
    const result = withoutThinking([user, mixed, onlyThinking, plain]);
    expect(result).toHaveLength(3);
    expect(result[0]).toBe(user);
    expect(result[1]).toEqual({ role: 'assistant', content: [{ type: 'text', text: 'Hello' }] });
    expect(result[2]).toBe(plain);
    expect(mixed.content).toHaveLength(3);
  });

  it('applies model, effort and executor changes on the next turn', async () => {
    const h = harness([reply({ text: 'One.' }), reply({ text: 'Two.' })]);
    await h.session.send('One');
    h.session.setModel('claude-haiku-5-5');
    h.session.setEffort('low');
    h.session.setExecutor(fakeExecutor({ supported: ['get_info'] }));
    expect(h.session.model).toBe('claude-haiku-5-5');
    expect(h.session.effort).toBe('low');
    await h.session.send('Two');
    const request = h.transport.requests[1];
    expect(request?.model).toBe('claude-haiku-5-5');
    expect(request?.effort).toBe('low');
    expect(request?.tools.map((tool) => tool.name)).toEqual(['get_info', 'ask_user', 'update_plan']);
  });

  it('ignores settings that are not valid values', () => {
    const h = harness([]);
    h.session.setModel('gpt-x' as never);
    h.session.setEffort('extreme' as never);
    h.session.setApprovalMode('never' as never);
    expect(h.session.model).toBe('claude-opus-5-5');
    expect(h.session.effort).toBe('high');
    expect(h.session.approvalMode).toBe('ask-destructive');
  });

  it('reset() clears the conversation', async () => {
    const h = harness([reply({ text: 'One.' }), reply({ text: 'Two.' })]);
    await h.session.send('One');
    expect(h.session.historySnapshot()).toHaveLength(2);
    h.session.reset();
    expect(h.session.historySnapshot()).toEqual([]);
    await h.session.send('Two');
    expect(h.transport.requests[1]?.messages).toHaveLength(1);
  });

  it('reset() during a turn stops it and leaves nothing behind', async () => {
    const h = harness([
      reply({ tools: [{ id: 'ask', name: 'ask_user', input: { questions: [{ header: 'A', question: 'Which?', options: [{ label: 'x' }, { label: 'y' }] }] } }] }),
      reply({ text: 'Fresh.' }),
    ]);
    const asked = h.next('question.asked');
    const turn = h.session.send('First');
    await asked;
    h.session.reset();
    await turn;
    expect(h.of('turn.finished')[0]?.stopReason).toBe('stopped');
    expect(h.session.historySnapshot()).toEqual([]);
    expect(h.session.busy).toBe(false);

    await h.session.send('Second');
    expect(h.transport.requests[1]?.messages).toHaveLength(1);
    expect(textsOf(h.transport.requests[1]?.messages[0])[0]).toBe('Second');
  });

  it('uses the API key transport lazily and only when no custom transport is given', async () => {
    // With a custom transport the key is irrelevant; nothing here may reach the network.
    const transport = fakeTransport([reply({ text: 'ok' })]);
    const session = new CopilotSession({ apiKey: 'sk-ant-test', model: 'claude-opus-5-5', effort: 'high', approvalMode: 'auto', executor: null, transport, toolSource: FAKE_SOURCE });
    session.setApiKey(null);
    session.setApiKey('sk-ant-other');
    await session.send('Hi');
    expect(transport.requests).toHaveLength(1);
  });
});
