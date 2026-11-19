import { describe, it, expect } from 'vitest';
import { z } from 'zod';
import { TOOL_CATALOG, TOOL_NAMES, getTool } from '@cadstudio/cad-protocol';
import { CopilotSession } from './session';
import { FAKE_SOURCE, FAKE_TOOLS, fakeExecutor, fakeTransport, okResult, reply, resultIsError, resultTextFor } from './test-support';
import {
  CATALOG_TOOL_SOURCE,
  LOCAL_TOOL_NAMES,
  LOCAL_TOOL_SPECS,
  buildModelTools,
  createToolSource,
  isLocalTool,
  offeredCadToolNames,
  parseAskUser,
  parseUpdatePlan,
} from './tools';
import type { SessionEvent } from './types';

const option = (label: string) => ({ label });
const question = (overrides: Record<string, unknown> = {}) => ({
  header: 'Size',
  question: 'How big?',
  options: [option('Small'), option('Large')],
  ...overrides,
});

describe('local tools', () => {
  it('are ask_user and update_plan, with closed object schemas', () => {
    expect(LOCAL_TOOL_NAMES).toEqual(['ask_user', 'update_plan']);
    expect(LOCAL_TOOL_SPECS.map((spec) => spec.name)).toEqual(['ask_user', 'update_plan']);
    expect(isLocalTool('ask_user')).toBe(true);
    expect(isLocalTool('update_plan')).toBe(true);
    expect(isLocalTool('create_line')).toBe(false);
    for (const spec of LOCAL_TOOL_SPECS) {
      expect(spec.inputSchema.type).toBe('object');
      expect(spec.inputSchema.additionalProperties).toBe(false);
      expect(spec.description.length).toBeGreaterThan(80);
    }
    // The local tools never collide with catalog names.
    expect(TOOL_NAMES.filter((name) => isLocalTool(name))).toEqual([]);
  });

  it('parseAskUser accepts 1 to 4 questions with 2 to 6 options', () => {
    const one = parseAskUser({ questions: [question()] });
    expect(one.ok).toBe(true);
    const four = parseAskUser({ questions: [question(), question(), question(), question()] });
    expect(four.ok && four.value.map((entry) => entry.id)).toEqual(['q1', 'q2', 'q3', 'q4']);
    const six = parseAskUser({ questions: [question({ options: ['a', 'b', 'c', 'd', 'e', 'f'].map(option) })] });
    expect(six.ok && six.value[0]?.options).toHaveLength(6);

    expect(parseAskUser({ questions: [] }).ok).toBe(false);
    expect(parseAskUser({ questions: Array.from({ length: 5 }, () => question()) }).ok).toBe(false);
    expect(parseAskUser({ questions: [question({ options: [option('only')] })] }).ok).toBe(false);
    expect(parseAskUser({ questions: [question({ options: ['a', 'b', 'c', 'd', 'e', 'f', 'g'].map(option) })] }).ok).toBe(false);
    expect(parseAskUser({ questions: [question({ header: '' })] }).ok).toBe(false);
    expect(parseAskUser({ questions: [question({ unexpected: 1 })] }).ok).toBe(false);
    expect(parseAskUser(undefined).ok).toBe(false);
    expect(parseAskUser('questions').ok).toBe(false);
  });

  it('parseAskUser normalises the questions for the UI', () => {
    const parsed = parseAskUser({
      questions: [
        question({
          header: '  wall type ',
          options: [{ label: ' Brick ', description: '  Common ', recommended: true }, { label: 'Block', description: '', recommended: false }],
          allowCustom: false,
        }),
        question(),
      ],
    });
    expect(parsed).toEqual({
      ok: true,
      value: [
        {
          id: 'q1',
          header: 'WALL TYPE',
          question: 'How big?',
          options: [{ label: 'Brick', description: 'Common', recommended: true }, { label: 'Block' }],
          allowCustom: false,
        },
        { id: 'q2', header: 'SIZE', question: 'How big?', options: [{ label: 'Small' }, { label: 'Large' }], allowCustom: true },
      ],
    });
  });

  it('parseAskUser reports readable problems', () => {
    const parsed = parseAskUser({ questions: [{ header: 'A', options: [option('x'), option('y')] }] });
    expect(parsed.ok).toBe(false);
    expect(!parsed.ok && parsed.errors.join('\n')).toContain('questions[0].question');
  });

  it('parseUpdatePlan accepts 1 to 12 tasks with known statuses and unique ids', () => {
    const tasks = Array.from({ length: 12 }, (_, index) => ({ id: `t${index}`, title: `Task ${index}`, status: 'pending' }));
    const parsed = parseUpdatePlan({ tasks });
    expect(parsed).toEqual({ ok: true, value: tasks });
    for (const status of ['pending', 'in_progress', 'done', 'failed']) {
      expect(parseUpdatePlan({ tasks: [{ id: 'a', title: 'A', status }] }).ok).toBe(true);
    }
    expect(parseUpdatePlan({ tasks: [] }).ok).toBe(false);
    expect(parseUpdatePlan({ tasks: [...tasks, { id: 'extra', title: 'Extra', status: 'pending' }] }).ok).toBe(false);
    expect(parseUpdatePlan({ tasks: [{ id: 'a', title: 'A', status: 'blocked' }] }).ok).toBe(false);
    expect(parseUpdatePlan({ tasks: [{ id: 'a', title: '', status: 'done' }] }).ok).toBe(false);
    expect(parseUpdatePlan({ tasks: [{ id: 'a', title: 'A', status: 'done', note: 'x' }] }).ok).toBe(false);
    const duplicate = parseUpdatePlan({ tasks: [{ id: 'a', title: 'A', status: 'done' }, { id: 'a', title: 'B', status: 'done' }] });
    expect(duplicate).toEqual({ ok: false, errors: ['tasks: every task needs a unique id (duplicated: "a")'] });
  });

  it('the JSON Schemas shown to the model state the same limits the parsers enforce', () => {
    const ask = LOCAL_TOOL_SPECS[0]?.inputSchema as { properties: { questions: { minItems: number; maxItems: number; items: { required: string[]; properties: { options: { minItems: number; maxItems: number } } } } } };
    expect(ask.properties.questions.minItems).toBe(1);
    expect(ask.properties.questions.maxItems).toBe(4);
    expect(ask.properties.questions.items.required).toEqual(['header', 'question', 'options']);
    expect(ask.properties.questions.items.properties.options.minItems).toBe(2);
    expect(ask.properties.questions.items.properties.options.maxItems).toBe(6);
    const plan = LOCAL_TOOL_SPECS[1]?.inputSchema as { properties: { tasks: { minItems: number; maxItems: number; items: { properties: { status: { enum: string[] } } } } } };
    expect(plan.properties.tasks.minItems).toBe(1);
    expect(plan.properties.tasks.maxItems).toBe(12);
    expect(plan.properties.tasks.items.properties.status.enum).toEqual(['pending', 'in_progress', 'done', 'failed']);
  });
});

describe('buildModelTools', () => {
  it('offers the supported tools in the source order, then the local tools', () => {
    const tools = buildModelTools(FAKE_SOURCE, ['draw_line', 'unknown_tool', 'get_info', 'draw_line', 'ask_user']);
    expect(tools.map((tool) => tool.name)).toEqual(['get_info', 'draw_line', 'ask_user', 'update_plan']);
    expect(offeredCadToolNames(FAKE_SOURCE, ['delete_things', 'get_info'])).toEqual(['get_info', 'delete_things']);
    expect(offeredCadToolNames(FAKE_SOURCE, null)).toEqual([]);
    expect(buildModelTools(FAKE_SOURCE, null).map((tool) => tool.name)).toEqual(['ask_user', 'update_plan']);
    expect(buildModelTools(FAKE_SOURCE, []).map((tool) => tool.name)).toEqual(['ask_user', 'update_plan']);
  });

  it('is deterministic: the same CAD always yields byte-identical definitions', () => {
    const a = JSON.stringify(buildModelTools(FAKE_SOURCE, ['get_info', 'draw_line']));
    const b = JSON.stringify(buildModelTools(FAKE_SOURCE, ['draw_line', 'get_info']));
    expect(a).toBe(b);
  });

  it('hands out copies of the local definitions', () => {
    const tools = buildModelTools(FAKE_SOURCE, null);
    (tools[0]?.inputSchema as { type: string }).type = 'array';
    expect(LOCAL_TOOL_SPECS[0]?.inputSchema.type).toBe('object');
    expect(buildModelTools(FAKE_SOURCE, null)[0]?.inputSchema.type).toBe('object');
  });
});

describe('createToolSource', () => {
  it('describes, validates and exports schemas from plain definitions', () => {
    expect(FAKE_SOURCE.names).toEqual(FAKE_TOOLS.map((tool) => tool.name));
    expect(FAKE_SOURCE.get('draw_line')?.risk).toBe('write');
    expect(FAKE_SOURCE.get('nope')).toBeUndefined();
    expect(FAKE_SOURCE.describe('draw_line', {})).toBe('Draw line');
    expect(FAKE_SOURCE.describe('nope', {})).toBe('Run nope');

    const schema = FAKE_SOURCE.jsonSchema(FAKE_SOURCE.get('draw_line')!);
    expect(schema.type).toBe('object');
    expect(schema).not.toHaveProperty('$schema');
    expect(Object.keys(schema.properties as object)).toEqual(['x1', 'y1', 'x2', 'y2', 'layer']);
    expect(schema.required).toEqual(['x1', 'y1', 'x2', 'y2']);

    expect(FAKE_SOURCE.validate('draw_line', { x1: 0, y1: 0, x2: 1, y2: 1 })).toEqual({
      ok: true,
      value: { x1: 0, y1: 0, x2: 1, y2: 1, layer: '0' },
    });
    const invalid = FAKE_SOURCE.validate('draw_line', { x1: 'a' });
    expect(invalid.ok).toBe(false);
    expect(!invalid.ok && invalid.errors.length).toBeGreaterThanOrEqual(4);
    expect(FAKE_SOURCE.validate('get_info', undefined)).toEqual({ ok: true, value: {} });
    expect(FAKE_SOURCE.validate('nope', {})).toEqual({ ok: false, errors: ['Unknown tool "nope"'] });
  });

  it('rejects duplicate names and non-object argument schemas', () => {
    const tool = FAKE_TOOLS[0]!;
    expect(() => createToolSource([tool, tool])).toThrow(/Duplicate tool definition "get_info"/);
    const odd = createToolSource([{ ...tool, name: 'odd', input: z.string() }]);
    expect(() => odd.jsonSchema(odd.get('odd')!)).toThrow(/must be an object schema/);
  });
});

describe('the shared catalog as a tool source', () => {
  it('lists the catalog in TOOL_NAMES order', () => {
    expect(CATALOG_TOOL_SOURCE.names).toEqual([...TOOL_NAMES]);
    expect(CATALOG_TOOL_SOURCE.names).toEqual(TOOL_CATALOG.map((tool) => tool.name));
    expect(CATALOG_TOOL_SOURCE.get('create_line')).toBe(getTool('create_line'));
  });

  it('builds a tool list in catalog order with closed object schemas', () => {
    const supported = ['delete_entities', 'zoom_extents', 'create_line', 'get_drawing_info', 'create_entities'];
    const tools = buildModelTools(CATALOG_TOOL_SOURCE, supported);
    expect(tools.map((tool) => tool.name)).toEqual([
      'get_drawing_info',
      'create_line',
      'create_entities',
      'delete_entities',
      'zoom_extents',
      'ask_user',
      'update_plan',
    ]);
    for (const tool of tools) {
      expect(tool.inputSchema.type).toBe('object');
      expect(tool.inputSchema.additionalProperties).toBe(false);
      expect(tool.description.length).toBeGreaterThan(20);
    }
    // The whole catalog can be offered, and twice gives the same bytes.
    const all = buildModelTools(CATALOG_TOOL_SOURCE, [...TOOL_NAMES].reverse());
    expect(all).toHaveLength(TOOL_NAMES.length + 2);
    expect(JSON.stringify(all)).toBe(JSON.stringify(buildModelTools(CATALOG_TOOL_SOURCE, TOOL_NAMES)));
  });

  it('runs real catalog tools through a session: validation, titles, summaries, approval of destructive tools', async () => {
    const cad = fakeExecutor({
      supported: ['get_drawing_info', 'create_line', 'delete_entities'],
      handlers: {
        create_line: (_call, self) => okResult((self.document?.revision ?? 0) + 1, { createdIds: ['L1'], data: { entities: [{ id: 'L1', type: 'line' }] } }),
        delete_entities: (call, self) => okResult((self.document?.revision ?? 0) + 2, { deletedIds: (call.args as { ids: string[] }).ids }),
      },
    });
    const transport = fakeTransport([
      reply({
        tools: [
          { id: 'line', name: 'create_line', input: { start: { x: 0, y: 0 }, end: { x: 12, y: 0 }, unit: 'ft' } },
          { id: 'bad', name: 'create_line', input: { start: { x: 0, y: 0 } } },
          { id: 'arc', name: 'create_arc', input: {} },
          { id: 'del', name: 'delete_entities', input: { ids: ['L1'] } },
        ],
      }),
      reply({ text: 'Done.' }),
    ]);
    const session = new CopilotSession({ apiKey: null, model: 'claude-opus-5-5', effort: 'high', approvalMode: 'auto', executor: cad, transport });
    const events: SessionEvent[] = [];
    session.on((event) => {
      events.push(event);
      if (event.type === 'approval.requested') session.resolveApproval(event.requestId, 'approve');
    });
    await session.send('Draw a 12 ft line, then delete it');

    expect(transport.requests[0]?.tools.map((tool) => tool.name)).toEqual(['get_drawing_info', 'create_line', 'delete_entities', 'ask_user', 'update_plan']);
    const started = events.flatMap((event) => (event.type === 'tool.started' ? [event] : []));
    expect(started.map((event) => event.name)).toEqual(['create_line', 'delete_entities']);
    expect(started[0]).toMatchObject({ title: 'Create line' });
    expect(started[0]?.summary).toContain('(0, 0)');
    expect(started[0]?.summary).toContain('ft');
    const approval = events.find((event) => event.type === 'approval.requested');
    expect(approval).toMatchObject({ name: 'delete_entities', title: 'Delete entities', risk: 'destructive', summary: 'Delete 1 object' });

    const history = session.historySnapshot();
    expect(resultIsError(history, 'line')).toBe(false);
    expect(resultIsError(history, 'bad')).toBe(true);
    expect(resultTextFor(history, 'bad')).toContain('end');
    expect(resultTextFor(history, 'arc')).toBe('CAD Studio does not support create_arc. Use one of the listed tools instead.');
    expect(resultIsError(history, 'del')).toBe(false);
    expect(cad.calls.map((call) => call.name)).toEqual(['create_line', 'delete_entities']);
    expect(cad.calls[0]?.opts.expectedRevision).toBe(7);
    expect(cad.calls[1]?.opts.expectedRevision).toBe(8);
  });
});
