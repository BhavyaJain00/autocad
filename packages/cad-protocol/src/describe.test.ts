import { describe, expect, it } from 'vitest';
import { TOOL_NAMES, describeToolCall } from './index';
import type { ToolName } from './index';

const P = { x: 0, y: 0 };
const Q = { x: 10, y: 5.5 };

/** For every tool: [arguments, expected line] pairs, the first with typical valid arguments. */
const EXPECTED: Record<ToolName, [args: unknown, line: string][]> = {
  get_drawing_info: [[{}, 'Read drawing info']],
  list_layers: [[{}, 'List layers']],
  inspect_entities: [
    [{ ids: ['a', 'b', 'c'] }, 'Inspect 3 objects'],
    [{ ids: ['a'] }, 'Inspect 1 object'],
    [{ types: ['line', 'circle'], layers: ['Walls'] }, 'Inspect objects of type line, circle on layer Walls'],
    [{ within: { min: P, max: Q } }, 'Inspect objects in a window'],
    [{}, 'Inspect objects'],
  ],
  get_selected_objects: [[{}, 'Read the selected objects']],
  find_objects: [
    [{ query: 'kitchen' }, 'Find objects matching "kitchen"'],
    [{ types: ['door'], near: { point: Q, radius: 3 } }, 'Find objects of type door near (10, 5.5)'],
    [{}, 'Find objects'],
  ],
  inspect_room: [
    [{ name: 'Kitchen' }, 'Inspect room "Kitchen"'],
    [{ roomId: 'room-7' }, 'Inspect room room-7'],
    [{}, 'Inspect room'],
  ],
  get_drawing_revision: [[{}, 'Check drawing revision']],

  create_line: [
    [{ start: P, end: Q, unit: 'ft' }, 'Create line from (0, 0) to (10, 5.5) ft'],
    [{ start: P, end: Q }, 'Create line from (0, 0) to (10, 5.5)'],
    [{ start: P }, 'Create line'],
  ],
  create_polyline: [
    [{ points: [P, Q, P] }, 'Create polyline with 3 points'],
    [{ points: [P, Q, P, Q], closed: true }, 'Create closed polyline with 4 points'],
    [{}, 'Create polyline'],
  ],
  create_rectangle: [
    [{ corner: P, width: 12, height: 14, unit: 'ft' }, 'Create rectangle 12 x 14 ft at (0, 0)'],
    [{ corner: Q, width: 3000, height: 4000.12345 }, 'Create rectangle 3000 x 4000.123 at (10, 5.5)'],
    [{ width: 12 }, 'Create rectangle'],
  ],
  create_circle: [
    [{ center: P, radius: 5, unit: 'm' }, 'Create circle radius 5 m at (0, 0)'],
    [{ center: P }, 'Create circle at (0, 0)'],
  ],
  create_arc: [
    [
      { center: P, radius: 2.5, startAngle: 0, endAngle: 90, unit: 'ft' },
      'Create arc radius 2.5 ft at (0, 0), 0 to 90 deg',
    ],
    [{}, 'Create arc'],
  ],
  create_text: [
    [{ position: P, text: 'Living Room', height: 1 }, 'Create text "Living Room" at (0, 0)'],
    [
      { position: P, text: 'A very long note that goes on and on and on forever', height: 1 },
      'Create text "A very long note that goes on and on ..." at (0, 0)',
    ],
    [{ position: P, text: 'two\nlines', height: 1 }, 'Create text "two lines" at (0, 0)'],
  ],
  create_entities: [
    [
      {
        entities: [
          { type: 'line', start: P, end: Q },
          { type: 'line', start: Q, end: P },
          { type: 'circle', center: P, radius: 1 },
        ],
      },
      'Create 3 objects (2 lines, 1 circle)',
    ],
    [{ entities: [{ type: 'text', position: P, text: 'a', height: 1 }] }, 'Create 1 object (1 text)'],
    [{ entities: [null, 5] }, 'Create 2 objects'],
    [{}, 'Create entities'],
  ],

  create_wall: [
    [
      { start: P, end: Q, thickness: 0.75, kind: 'external', unit: 'ft' },
      'Create external wall from (0, 0) to (10, 5.5) ft, 0.75 ft thick',
    ],
    [{ start: P, end: Q, thickness: 230 }, 'Create wall from (0, 0) to (10, 5.5), 230 thick'],
  ],
  create_room: [
    [
      { name: 'Bedroom', width: 12, length: 14, origin: P, unit: 'ft' },
      'Create room "Bedroom" 12 x 14 ft at (0, 0)',
    ],
    [{ name: 'Bedroom', width: 12, length: 14 }, 'Create room "Bedroom" 12 x 14'],
    [{}, 'Create room'],
  ],
  create_door: [
    [{ position: Q, width: 3, unit: 'ft' }, 'Create door 3 ft wide at (10, 5.5)'],
    [{ position: Q, width: 900, doorType: 'main_entrance' }, 'Create main entrance door 900 wide at (10, 5.5)'],
    [{ position: Q, width: 900, doorType: 'single' }, 'Create door 900 wide at (10, 5.5)'],
  ],
  create_window: [
    [{ position: Q, width: 4, windowType: 'sliding', unit: 'ft' }, 'Create sliding window 4 ft wide at (10, 5.5)'],
    [{ position: Q, width: 4 }, 'Create window 4 wide at (10, 5.5)'],
  ],
  create_staircase: [
    [{ origin: P, width: 3, length: 10, steps: 12, unit: 'ft' }, 'Create staircase 3 x 10 ft at (0, 0), 12 steps'],
    [{ origin: P, width: 3, length: 10 }, 'Create staircase 3 x 10 at (0, 0)'],
  ],
  create_column: [
    [{ center: P, shape: 'rectangular', width: 1, depth: 1.5, unit: 'ft' }, 'Create column 1 x 1.5 ft at (0, 0)'],
    [{ center: P, shape: 'circular', diameter: 400, unit: 'mm' }, 'Create circular column diameter 400 mm at (0, 0)'],
  ],
  create_plot_boundary: [
    [{ width: 30, length: 40, unit: 'ft' }, 'Create plot boundary 30 x 40 ft'],
    [{ width: 30, length: 40, origin: P }, 'Create plot boundary 30 x 40 at (0, 0)'],
  ],
  create_floor_plan: [
    [
      {
        plot: { width: 30, length: 40 },
        rooms: [
          { name: 'A', roomType: 'bedroom' },
          { name: 'B', roomType: 'kitchen' },
        ],
        unit: 'ft',
      },
      'Create floor plan with 2 rooms on a 30 x 40 ft plot',
    ],
    [
      { plot: { width: 9, length: 12 }, rooms: [{ name: 'A', roomType: 'bedroom' }], previewOnly: true, unit: 'm' },
      'Preview floor plan with 1 room on a 9 x 12 m plot',
    ],
    [{}, 'Create floor plan'],
  ],

  move_entities: [
    [{ ids: ['a', 'b'], dx: 3, dy: 0, unit: 'ft' }, 'Move 2 objects by (3, 0) ft'],
    [{ ids: ['a'], dx: -1.25, dy: 2 }, 'Move 1 object by (-1.25, 2)'],
    [{ dx: 1 }, 'Move objects'],
  ],
  copy_entities: [
    [{ ids: ['a', 'b'], dx: 3, dy: 0, unit: 'ft' }, 'Copy 2 objects by (3, 0) ft'],
    [{ ids: ['a'], dx: 1000, dy: 0, count: 3 }, 'Copy 1 object by (1000, 0), 3 times'],
  ],
  rotate_entities: [
    [{ ids: ['a', 'b'], center: P, angle: 90 }, 'Rotate 2 objects by 90 deg about (0, 0)'],
    [{ ids: ['a'] }, 'Rotate 1 object'],
  ],
  scale_entities: [
    [{ ids: ['a', 'b', 'c'], center: Q, factor: 2 }, 'Scale 3 objects by 2x about (10, 5.5)'],
    [{ ids: ['a'], center: P, factor: 0.5 }, 'Scale 1 object by 0.5x about (0, 0)'],
  ],
  mirror_entities: [
    [{ ids: ['a', 'b'], lineStart: P, lineEnd: Q }, 'Mirror 2 objects across the line (0, 0) to (10, 5.5)'],
    [
      { ids: ['a'], lineStart: P, lineEnd: Q, keepOriginal: true },
      'Mirror 1 object across the line (0, 0) to (10, 5.5), keeping the originals',
    ],
  ],
  update_entity_properties: [
    [{ ids: ['a', 'b', 'c'], layer: 'Walls', color: '#ff0000' }, 'Set layer "Walls" and color #ff0000 on 3 objects'],
    [{ ids: ['a'], color: 'bylayer' }, 'Set color bylayer on 1 object'],
    [{ ids: ['a'] }, 'Update properties of 1 object'],
  ],
  move_room: [
    [{ name: 'Kitchen', dx: 3, dy: 0, unit: 'ft' }, 'Move room "Kitchen" by (3, 0) ft'],
    [{ roomId: 'r1', dx: 0, dy: -2 }, 'Move room r1 by (0, -2)'],
  ],
  resize_room: [
    [{ name: 'Kitchen', width: 12, length: 14, unit: 'ft' }, 'Resize room "Kitchen" to 12 x 14 ft'],
    [{ name: 'Kitchen', width: 12, unit: 'ft' }, 'Resize room "Kitchen" to width 12 ft'],
    [{ roomId: 'r1', length: 4000 }, 'Resize room r1 to length 4000'],
    [{ roomId: 'r1' }, 'Resize room r1'],
  ],
  delete_entities: [
    [{ ids: ['a', 'b', 'c'] }, 'Delete 3 objects'],
    [{ ids: ['a'] }, 'Delete 1 object'],
    [{}, 'Delete objects'],
  ],

  add_dimension: [
    [{ kind: 'linear', from: P, to: Q, offset: 2, unit: 'ft' }, 'Add linear dimension from (0, 0) to (10, 5.5) ft'],
    [{ kind: 'aligned', from: P, to: Q, offset: 2 }, 'Add aligned dimension from (0, 0) to (10, 5.5)'],
  ],
  add_room_labels: [
    [{}, 'Add labels to all rooms'],
    [{ roomIds: ['r1', 'r2'] }, 'Add labels to 2 rooms'],
    [{ roomIds: ['r1'] }, 'Add labels to 1 room'],
  ],

  create_layer: [
    [{ name: 'Walls' }, 'Create layer "Walls"'],
    [{}, 'Create layer'],
  ],
  set_current_layer: [
    [{ name: 'Walls' }, 'Set current layer to "Walls"'],
    [{}, 'Set current layer'],
  ],
  set_layer_state: [
    [{ name: 'Walls', visible: false }, 'Update layer "Walls": hide'],
    [
      { name: 'Walls', visible: true, locked: true, color: '#00ff00', newName: 'Outer' },
      'Update layer "Walls": show, lock, color #00ff00, rename to "Outer"',
    ],
    [{ name: 'Walls', locked: false }, 'Update layer "Walls": unlock'],
    [{ name: 'Walls' }, 'Update layer "Walls"'],
  ],

  validate_drawing: [
    [{}, 'Validate drawing'],
    [{ checks: ['overlaps', 'zero_length'] }, 'Validate drawing (overlaps, zero_length)'],
  ],

  zoom_extents: [[{}, 'Zoom to extents']],
  zoom_to_entities: [[{ ids: ['a', 'b'] }, 'Zoom to 2 objects']],
  highlight_entities: [[{ ids: ['a'] }, 'Highlight 1 object']],
  clear_highlight: [[{}, 'Clear highlight']],

  undo_last_operation: [[{}, 'Undo last operation']],
  save_document: [[{}, 'Save drawing']],
};

const GARBAGE: unknown[] = [
  undefined,
  null,
  0,
  42,
  Number.NaN,
  '',
  'text',
  true,
  [],
  [1, 2, 3],
  () => 1,
  Symbol('s'),
  { ids: 'nope', dx: 'a', dy: {}, unit: 7 },
  { ids: [null], start: 5, end: 'x', points: {}, entities: 'many', rooms: 3, plot: [] },
  { name: 12, text: {}, width: 'wide', height: null, radius: Number.POSITIVE_INFINITY, center: { x: Number.NaN, y: 1 } },
  { near: 'here', within: 4, checks: [1, 2], roomIds: {}, types: [{}], layers: [[]] },
  Object.create(null) as unknown,
];

describe('describeToolCall', () => {
  it('has expectations for every tool', () => {
    expect(Object.keys(EXPECTED).sort()).toEqual([...TOOL_NAMES].sort());
  });

  describe.each(TOOL_NAMES.map((name) => [name] as const))('%s', (name) => {
    it('produces the expected line', () => {
      for (const [args, line] of EXPECTED[name]) {
        expect(describeToolCall(name, args)).toBe(line);
      }
    });

    it('never throws and stays a short single line for garbage arguments', () => {
      for (const args of GARBAGE) {
        const line = describeToolCall(name, args);
        expect(typeof line).toBe('string');
        expect(line.length).toBeGreaterThan(3);
        expect(line.length).toBeLessThanOrEqual(140);
        expect(line).not.toMatch(/[\r\n]/);
        expect(line).not.toMatch(/undefined|NaN|\[object|Infinity/);
        expect(line[0]).toBe(line[0]?.toUpperCase());
      }
    });
  });

  it('matches the documented examples', () => {
    expect(describeToolCall('create_rectangle', { corner: { x: 0, y: 0 }, width: 12, height: 14, unit: 'ft' })).toBe(
      'Create rectangle 12 x 14 ft at (0, 0)',
    );
    expect(describeToolCall('delete_entities', { ids: ['a', 'b', 'c'] })).toBe('Delete 3 objects');
    expect(describeToolCall('move_entities', { ids: ['a', 'b'], dx: 3, dy: 0, unit: 'ft' })).toBe(
      'Move 2 objects by (3, 0) ft',
    );
    expect(describeToolCall('create_layer', { name: 'Walls' })).toBe('Create layer "Walls"');
  });

  it('says "Run <name>" for unknown tools', () => {
    expect(describeToolCall('explode_everything', { ids: ['a'] })).toBe('Run explode_everything');
    expect(describeToolCall('ask_user', undefined)).toBe('Run ask_user');
    expect(describeToolCall('', {})).toBe('Run unknown tool');
    expect(describeToolCall('toString', {})).toBe('Run toString');
    expect(describeToolCall(undefined as unknown as string, {})).toBe('Run unknown tool');
    expect(describeToolCall('x'.repeat(500), {}).length).toBeLessThanOrEqual(64);
  });

  it('formats numbers compactly', () => {
    expect(describeToolCall('move_entities', { ids: ['a'], dx: 0.1 + 0.2, dy: -0 })).toBe('Move 1 object by (0.3, 0)');
    expect(describeToolCall('move_entities', { ids: ['a'], dx: 1234567.891, dy: -0.0004 })).toBe(
      'Move 1 object by (1234567.891, 0)',
    );
    expect(describeToolCall('create_circle', { center: { x: 1e-9, y: 2.5 }, radius: 1 / 3 })).toBe(
      'Create circle radius 0.333 at (0, 2.5)',
    );
  });

  it('ignores an invalid unit instead of printing it', () => {
    expect(describeToolCall('create_rectangle', { corner: P, width: 1, height: 2, unit: 'parsecs' })).toBe(
      'Create rectangle 1 x 2 at (0, 0)',
    );
    expect(describeToolCall('create_rectangle', { corner: P, width: 1, height: 2, unit: 5 })).toBe(
      'Create rectangle 1 x 2 at (0, 0)',
    );
  });

  it('keeps very long names and lists short', () => {
    const line = describeToolCall('create_layer', { name: 'L'.repeat(300) });
    expect(line.length).toBeLessThan(60);
    expect(line.endsWith('..."')).toBe(true);
    const many = describeToolCall('inspect_entities', { types: ['a', 'b', 'c', 'd', 'e'] });
    expect(many).toBe('Inspect objects of type a, b, c and 2 more');
    const batch = describeToolCall('create_entities', {
      entities: Array.from({ length: 500 }, (_, index) => ({ type: index % 2 === 0 ? 'line' : 'arc' })),
    });
    expect(batch).toBe('Create 500 objects (250 lines, 250 arcs)');
  });

  it('survives hostile objects', () => {
    const throwing = new Proxy(
      {},
      {
        get() {
          throw new Error('boom');
        },
      },
    );
    expect(describeToolCall('create_rectangle', throwing)).toBe('Create rectangle');
    expect(describeToolCall('delete_entities', throwing)).toBe('Delete entities');
  });
});
