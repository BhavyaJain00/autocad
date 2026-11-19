import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import {
  TOOLS,
  TOOL_CATALOG,
  TOOL_CATEGORIES,
  TOOL_NAMES,
  getTool,
  isToolName,
  toolJsonSchema,
} from '../index';
import type { ToolCategory, ToolInput, ToolName, ToolOutput, ToolRisk } from '../index';

type Json = Record<string, unknown>;

const ALLOWED_KEYWORDS = new Set([
  'type',
  'enum',
  'const',
  'properties',
  'required',
  'items',
  'anyOf',
  'minimum',
  'maximum',
  'exclusiveMinimum',
  'minItems',
  'maxItems',
  'minLength',
  'maxLength',
  'description',
  'default',
  'additionalProperties',
]);

const EXPECTED: Record<ToolName, { category: ToolCategory; risk: ToolRisk }> = {
  get_drawing_info: { category: 'inspection', risk: 'read' },
  list_layers: { category: 'inspection', risk: 'read' },
  inspect_entities: { category: 'inspection', risk: 'read' },
  get_selected_objects: { category: 'inspection', risk: 'read' },
  find_objects: { category: 'inspection', risk: 'read' },
  inspect_room: { category: 'inspection', risk: 'read' },
  get_drawing_revision: { category: 'inspection', risk: 'read' },
  create_line: { category: 'geometry', risk: 'write' },
  create_polyline: { category: 'geometry', risk: 'write' },
  create_rectangle: { category: 'geometry', risk: 'write' },
  create_circle: { category: 'geometry', risk: 'write' },
  create_arc: { category: 'geometry', risk: 'write' },
  create_text: { category: 'geometry', risk: 'write' },
  create_entities: { category: 'geometry', risk: 'write' },
  create_wall: { category: 'architecture', risk: 'write' },
  create_room: { category: 'architecture', risk: 'write' },
  create_door: { category: 'architecture', risk: 'write' },
  create_window: { category: 'architecture', risk: 'write' },
  create_staircase: { category: 'architecture', risk: 'write' },
  create_column: { category: 'architecture', risk: 'write' },
  create_plot_boundary: { category: 'architecture', risk: 'write' },
  create_floor_plan: { category: 'architecture', risk: 'write' },
  move_entities: { category: 'modification', risk: 'write' },
  copy_entities: { category: 'modification', risk: 'write' },
  rotate_entities: { category: 'modification', risk: 'write' },
  scale_entities: { category: 'modification', risk: 'write' },
  mirror_entities: { category: 'modification', risk: 'write' },
  update_entity_properties: { category: 'modification', risk: 'write' },
  move_room: { category: 'modification', risk: 'write' },
  resize_room: { category: 'modification', risk: 'write' },
  delete_entities: { category: 'modification', risk: 'destructive' },
  add_dimension: { category: 'annotation', risk: 'write' },
  add_room_labels: { category: 'annotation', risk: 'write' },
  create_layer: { category: 'layers', risk: 'write' },
  set_current_layer: { category: 'layers', risk: 'write' },
  set_layer_state: { category: 'layers', risk: 'write' },
  validate_drawing: { category: 'validation', risk: 'read' },
  zoom_extents: { category: 'view', risk: 'read' },
  zoom_to_entities: { category: 'view', risk: 'read' },
  highlight_entities: { category: 'view', risk: 'read' },
  clear_highlight: { category: 'view', risk: 'read' },
  undo_last_operation: { category: 'document', risk: 'write' },
  save_document: { category: 'document', risk: 'destructive' },
};

function isJson(value: unknown): value is Json {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Walks a JSON Schema and reports every violation of the export rules. */
function auditSchema(node: unknown, path: string, problems: string[], isProperty: boolean): void {
  if (!isJson(node)) {
    problems.push(`${path}: not a schema object`);
    return;
  }
  for (const key of Object.keys(node)) {
    if (!ALLOWED_KEYWORDS.has(key)) problems.push(`${path}: unsupported keyword "${key}"`);
  }
  if (isProperty && (typeof node.description !== 'string' || node.description.trim().length < 8)) {
    problems.push(`${path}: property without a description`);
  }
  if (node.type === undefined && node.anyOf === undefined) problems.push(`${path}: no type`);
  if (node.type === 'object') {
    if (node.additionalProperties !== false) problems.push(`${path}: additionalProperties is not false`);
    if (!Array.isArray(node.required)) problems.push(`${path}: no required list`);
    if (!isJson(node.properties)) {
      problems.push(`${path}: no properties`);
    } else {
      const required = Array.isArray(node.required) ? node.required : [];
      for (const name of required) {
        if (!(String(name) in node.properties)) problems.push(`${path}: required "${String(name)}" is not a property`);
      }
      for (const [name, child] of Object.entries(node.properties)) {
        auditSchema(child, `${path}.${name}`, problems, true);
      }
    }
  }
  if (node.type === 'array') {
    if (node.items === undefined) problems.push(`${path}: array without items`);
    else auditSchema(node.items, `${path}[]`, problems, false);
  }
  if (node.anyOf !== undefined) {
    if (!Array.isArray(node.anyOf) || node.anyOf.length === 0) problems.push(`${path}: empty anyOf`);
    else node.anyOf.forEach((option, index) => auditSchema(option, `${path}|${index}`, problems, false));
  }
}

describe('tool catalog integrity', () => {
  it('defines every tool name exactly once, in TOOL_NAMES order', () => {
    expect(TOOL_NAMES.length).toBe(43);
    expect(new Set(TOOL_NAMES).size).toBe(TOOL_NAMES.length);
    expect(TOOL_CATALOG.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);
    expect(Object.keys(TOOLS).sort()).toEqual([...TOOL_NAMES].sort());
  });

  it('keys TOOLS by each definition name and shares the instances with TOOL_CATALOG', () => {
    for (const [index, name] of TOOL_NAMES.entries()) {
      expect(TOOLS[name].name).toBe(name);
      expect(TOOL_CATALOG[index]).toBe(TOOLS[name]);
      expect(getTool(name)).toBe(TOOLS[name]);
    }
  });

  it('looks up tools by name and rejects unknown names', () => {
    expect(getTool('create_line')?.title).toBe('Create line');
    expect(getTool('nope')).toBeUndefined();
    expect(getTool('')).toBeUndefined();
    expect(getTool('toString')).toBeUndefined();
    expect(getTool('constructor')).toBeUndefined();
    expect(isToolName('delete_entities')).toBe(true);
    expect(isToolName('Delete_Entities')).toBe(false);
    expect(isToolName(42)).toBe(false);
  });

  it.each(TOOL_NAMES.map((name) => [name] as const))('%s has complete metadata', (name) => {
    const tool = TOOLS[name];
    expect(tool.name).toMatch(/^[a-z]+(_[a-z]+)+$/);
    expect(tool.title.length).toBeGreaterThan(3);
    expect(tool.title[0]).toBe(tool.title[0]?.toUpperCase());
    expect(tool.description.length).toBeGreaterThan(60);
    expect(tool.description).not.toMatch(/\s{2,}/);
    expect(tool.category).toBe(EXPECTED[name].category);
    expect(tool.risk).toBe(EXPECTED[name].risk);
    expect(TOOL_CATEGORIES).toContain(tool.category);
    expect(typeof tool.input.safeParse).toBe('function');
    expect(typeof tool.output.safeParse).toBe('function');
  });

  it('uses unique titles and descriptions', () => {
    expect(new Set(TOOL_CATALOG.map((tool) => tool.title)).size).toBe(TOOL_CATALOG.length);
    expect(new Set(TOOL_CATALOG.map((tool) => tool.description)).size).toBe(TOOL_CATALOG.length);
  });

  it('marks exactly delete_entities and save_document as destructive', () => {
    const destructive = TOOL_CATALOG.filter((tool) => tool.risk === 'destructive').map((tool) => tool.name);
    expect(destructive.sort()).toEqual(['delete_entities', 'save_document']);
  });

  it('keeps categories contiguous in catalog order', () => {
    const order: string[] = [];
    for (const tool of TOOL_CATALOG) {
      if (order[order.length - 1] !== tool.category) order.push(tool.category);
    }
    expect(order).toEqual([...TOOL_CATEGORIES]);
  });

  it('mentions the unit convention in every tool that takes a unit', () => {
    for (const tool of TOOL_CATALOG) {
      const schema = toolJsonSchema(tool);
      const properties = schema.properties as Json;
      if ('unit' in properties) {
        expect(tool.description, tool.name).toMatch(/"unit"/);
        expect((properties.unit as Json).enum).toEqual(['mm', 'cm', 'm', 'in', 'ft']);
      }
    }
  });

  it('freezes the catalog and its definitions', () => {
    expect(Object.isFrozen(TOOL_CATALOG)).toBe(true);
    expect(Object.isFrozen(TOOLS.create_line)).toBe(true);
  });
});

describe('toolJsonSchema', () => {
  it.each(TOOL_NAMES.map((name) => [name] as const))('%s exports a strict, fully described schema', (name) => {
    const schema = toolJsonSchema(TOOLS[name]);
    expect(schema.type).toBe('object');
    expect(schema.additionalProperties).toBe(false);
    expect(Array.isArray(schema.required)).toBe(true);
    expect(isJson(schema.properties)).toBe(true);
    expect(schema).not.toHaveProperty('$schema');
    expect(schema).not.toHaveProperty('$id');
    const problems: string[] = [];
    auditSchema(schema, name, problems, false);
    expect(problems).toEqual([]);
    // Must survive a JSON round trip unchanged (no undefined, functions or cycles).
    expect(JSON.parse(JSON.stringify(schema))).toEqual(schema);
  });

  it('gives tools without arguments an empty closed object', () => {
    for (const name of ['get_drawing_info', 'list_layers', 'zoom_extents', 'clear_highlight', 'save_document'] as const) {
      expect(toolJsonSchema(TOOLS[name])).toEqual({
        type: 'object',
        properties: {},
        required: [],
        additionalProperties: false,
      });
    }
  });

  it('lists exactly the required arguments', () => {
    expect(toolJsonSchema(TOOLS.create_line).required).toEqual(['start', 'end']);
    expect(toolJsonSchema(TOOLS.create_rectangle).required).toEqual(['corner', 'width', 'height']);
    expect(toolJsonSchema(TOOLS.create_text).required).toEqual(['position', 'text', 'height']);
    expect(toolJsonSchema(TOOLS.inspect_entities).required).toEqual([]);
    expect(toolJsonSchema(TOOLS.copy_entities).required).toEqual(['ids', 'dx', 'dy']);
    expect(toolJsonSchema(TOOLS.create_floor_plan).required).toEqual(['plot', 'rooms']);
    expect(toolJsonSchema(TOOLS.delete_entities).required).toEqual(['ids']);
  });

  it('exports numeric limits, defaults and enums', () => {
    const inspect = toolJsonSchema(TOOLS.inspect_entities).properties as Json;
    expect(inspect.limit).toMatchObject({ type: 'integer', minimum: 1, maximum: 200, default: 50 });
    expect(inspect.offset).toMatchObject({ type: 'integer', minimum: 0, default: 0 });
    expect(inspect.ids).toMatchObject({ type: 'array', minItems: 1, maxItems: 500 });
    expect((inspect.ids as Json).items).toMatchObject({ type: 'string', minLength: 1, maxLength: 128 });

    const rectangle = toolJsonSchema(TOOLS.create_rectangle).properties as Json;
    expect(rectangle.width).toMatchObject({ type: 'number', exclusiveMinimum: 0 });
    expect(rectangle.corner).toMatchObject({ type: 'object', required: ['x', 'y'], additionalProperties: false });

    const copy = toolJsonSchema(TOOLS.copy_entities).properties as Json;
    expect(copy.count).toMatchObject({ type: 'integer', minimum: 1, maximum: 100, default: 1 });

    const stairs = toolJsonSchema(TOOLS.create_staircase).properties as Json;
    expect(stairs.steps).toMatchObject({ type: 'integer', minimum: 2, maximum: 60 });
    expect(stairs.direction).toMatchObject({ type: 'number', default: 90 });

    const room = toolJsonSchema(TOOLS.create_room).properties as Json;
    expect((room.roomType as Json).enum).toContain('master_bedroom');
    expect((room.roomType as Json).enum).toHaveLength(15);

    const polyline = toolJsonSchema(TOOLS.create_polyline).properties as Json;
    expect(polyline.points).toMatchObject({ type: 'array', minItems: 2, maxItems: 2000 });

    const text = toolJsonSchema(TOOLS.create_text).properties as Json;
    expect(text.text).toMatchObject({ type: 'string', minLength: 1, maxLength: 2000 });

    const layer = toolJsonSchema(TOOLS.create_layer).properties as Json;
    expect(layer.name).toMatchObject({ type: 'string', minLength: 1, maxLength: 255 });

    const setbacks = (toolJsonSchema(TOOLS.create_plot_boundary).properties as Json).setbacks as Json;
    expect((setbacks.properties as Json).front).toMatchObject({ type: 'number', minimum: 0 });
  });

  it('exports create_entities items as an anyOf of six tagged strict objects', () => {
    const schema = toolJsonSchema(TOOLS.create_entities);
    const entities = (schema.properties as Json).entities as Json;
    expect(entities).toMatchObject({ type: 'array', minItems: 1, maxItems: 500 });
    const options = (entities.items as Json).anyOf as Json[];
    expect(options).toHaveLength(6);
    const tags = options.map((option) => ((option.properties as Json).type as Json).const);
    expect(tags).toEqual(['line', 'polyline', 'rectangle', 'circle', 'arc', 'text']);
    for (const option of options) {
      expect(option.additionalProperties).toBe(false);
      expect(option.required).toContain('type');
      expect(option.properties).not.toHaveProperty('unit');
      expect(option.properties).toHaveProperty('layer');
      expect(option.properties).toHaveProperty('color');
    }
    expect(JSON.stringify(schema)).not.toContain('oneOf');
  });

  it('returns an independent copy on every call', () => {
    const first = toolJsonSchema(TOOLS.create_circle);
    (first.properties as Json).center = 'mutated';
    (first.required as string[]).push('bogus');
    const second = toolJsonSchema(TOOLS.create_circle);
    expect((second.properties as Json).center).toMatchObject({ type: 'object' });
    expect(second.required).toEqual(['center', 'radius']);
  });

  it('never leaks pattern, format or $-keywords', () => {
    for (const tool of TOOL_CATALOG) {
      const text = JSON.stringify(toolJsonSchema(tool));
      expect(text).not.toMatch(/"(pattern|format|\$schema|\$id|\$ref|\$defs|oneOf|allOf)"/);
    }
  });
});

describe('inferred tool types', () => {
  it('types inputs and outputs from the schemas', () => {
    const rectangle: ToolInput<'create_rectangle'> = { corner: { x: 0, y: 0 }, width: 1, height: 2 };
    const inspect: ToolInput<'inspect_entities'> = { limit: 50, offset: 0 };
    const info: ToolOutput<'get_drawing_revision'> = { revision: 3 };
    const created: ToolOutput<'create_line'> = { entities: [{ id: 'e1', type: 'line' }] };
    expect(TOOLS.create_rectangle.input.parse(rectangle)).toEqual(rectangle);
    expect(TOOLS.inspect_entities.input.parse({})).toEqual(inspect);
    expect(TOOLS.get_drawing_revision.output.parse(info)).toEqual(info);
    expect(TOOLS.create_line.output.parse(created)).toEqual(created);
  });
});

describe('output schemas', () => {
  it('accept the documented result shapes and keep CAD-specific extras', () => {
    const summary = {
      id: 'e1',
      type: 'line',
      layer: '0',
      color: 'bylayer',
      bbox: { min: { x: 0, y: 0 }, max: { x: 10, y: 0 } },
      length: 10,
      geometry: { start: { x: 0, y: 0 }, end: { x: 10, y: 0 } },
      lineweight: 0.25,
    };
    const list = { total: 1, entities: [summary], truncated: false };
    expect(TOOLS.inspect_entities.output.parse(list)).toEqual(list);
    expect(TOOLS.get_selected_objects.output.parse(list)).toEqual(list);
    expect(TOOLS.find_objects.output.parse(list)).toEqual(list);

    const info = {
      name: 'Plan',
      path: null,
      unit: 'ft',
      entityCount: 2,
      entityCountsByType: { line: 2 },
      layerCount: 1,
      currentLayer: '0',
      extents: null,
      selectionCount: 0,
      revision: 7,
    };
    expect(TOOLS.get_drawing_info.output.parse(info)).toEqual(info);
    expect(TOOLS.get_drawing_info.output.safeParse({ ...info, unit: 'yards' }).success).toBe(false);

    const layers = { layers: [{ name: '0', color: '#ffffff', visible: true, locked: false, current: true, entityCount: 2 }] };
    expect(TOOLS.list_layers.output.parse(layers)).toEqual(layers);

    const room = {
      name: 'Kitchen',
      roomType: 'kitchen',
      width: 10,
      length: 12,
      area: 120,
      origin: { x: 0, y: 0 },
      wallIds: ['w1'],
      doorIds: [],
      windowIds: [],
    };
    expect(TOOLS.inspect_room.output.parse(room)).toEqual(room);

    const plan = {
      layout: [{ name: 'Kitchen', roomType: 'kitchen', origin: { x: 0, y: 0 }, width: 10, length: 12, area: 120 }],
      issues: ['Bedroom 2 is smaller than requested'],
      created: false,
    };
    expect(TOOLS.create_floor_plan.output.parse(plan)).toEqual(plan);

    const validation = {
      issues: [{ severity: 'warning', code: 'overlap', message: 'Two lines overlap', entityIds: ['a', 'b'] }],
      checked: ['overlaps'],
    };
    expect(TOOLS.validate_drawing.output.parse(validation)).toEqual(validation);
    expect(
      TOOLS.validate_drawing.output.safeParse({
        issues: [{ severity: 'fatal', code: 'x', message: 'm', entityIds: [] }],
        checked: [],
      }).success,
    ).toBe(false);

    expect(TOOLS.undo_last_operation.output.parse({ undone: null })).toEqual({ undone: null });
    expect(TOOLS.undo_last_operation.output.parse({ undone: 'Create line' })).toEqual({ undone: 'Create line' });
    expect(TOOLS.save_document.output.parse({ path: null, saved: false })).toEqual({ path: null, saved: false });
    expect(TOOLS.save_document.output.safeParse({ saved: true }).success).toBe(false);
    expect(TOOLS.zoom_extents.output.parse({})).toEqual({});
    expect(TOOLS.delete_entities.output.parse({ deletedCount: 3 })).toEqual({ deletedCount: 3 });
  });

  it.each(TOOL_NAMES.map((name) => [name] as const))('%s output is an object schema that converts to JSON Schema', (name) => {
    const schema = TOOLS[name].output;
    expect(schema.safeParse('not an object').success).toBe(false);
    expect(schema.safeParse(null).success).toBe(false);
    const json = z.toJSONSchema(schema) as Json;
    expect(json.type).toBe('object');
  });
});
