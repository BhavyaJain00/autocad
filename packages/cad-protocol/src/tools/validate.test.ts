import { describe, expect, it } from 'vitest';
import { MAX_VALIDATION_ERRORS, TOOLS, TOOL_NAMES, validateToolArguments } from '../index';
import type { ToolName } from '../index';

const P = { x: 0, y: 0 };
const Q = { x: 10, y: 5.5 };
const IDS = ['e1', 'e2'];
const ids = (count: number): string[] => Array.from({ length: count }, (_, index) => `id-${index}`);
const points = (count: number): { x: number; y: number }[] =>
  Array.from({ length: count }, (_, index) => ({ x: index, y: index % 7 }));

interface ToolCases {
  /** Argument objects that must be accepted (the first is the minimal one). */
  valid: unknown[];
  /** Argument objects that must be rejected, with a pattern one of the error strings must match. */
  invalid: [args: unknown, error: RegExp][];
}

const NO_ARGUMENTS: ToolCases = {
  valid: [{}, undefined, null],
  invalid: [
    [{ extra: 1 }, /^Unknown argument "extra"$/],
    [{ unit: 'mm' }, /^Unknown argument "unit"$/],
    ['text', /expected object, received string/],
    [[], /expected object, received array/],
    [42, /expected object, received number/],
  ],
};

const ID_LIST_ONLY: ToolCases = {
  valid: [{ ids: ['a'] }, { ids: ids(500) }],
  invalid: [
    [{}, /^ids: is required \(expected array\)$/],
    [{ ids: [] }, /^ids: Too small/],
    [{ ids: ids(501) }, /^ids: Too big/],
    [{ ids: 'a' }, /^ids: Invalid input: expected array, received string$/],
    [{ ids: [''] }, /^ids\[0\]: Too small/],
    [{ ids: ['x'.repeat(129)] }, /^ids\[0\]: Too big/],
    [{ ids: [7] }, /^ids\[0\]: Invalid input: expected string, received number$/],
    [{ ids: ['a'], unit: 'mm' }, /^Unknown argument "unit"$/],
  ],
};

const CASES: Record<ToolName, ToolCases> = {
  get_drawing_info: NO_ARGUMENTS,
  list_layers: NO_ARGUMENTS,
  get_drawing_revision: NO_ARGUMENTS,
  zoom_extents: NO_ARGUMENTS,
  clear_highlight: NO_ARGUMENTS,
  undo_last_operation: NO_ARGUMENTS,
  save_document: NO_ARGUMENTS,

  inspect_entities: {
    valid: [
      {},
      { ids: IDS },
      {
        ids: IDS,
        types: ['line', 'circle'],
        layers: ['Walls'],
        within: { min: P, max: Q },
        limit: 200,
        offset: 40,
        unit: 'ft',
      },
      { limit: 1, offset: 0 },
    ],
    invalid: [
      [{ limit: 0 }, /^limit: Too small/],
      [{ limit: 201 }, /^limit: Too big/],
      [{ limit: 2.5 }, /^limit: Invalid input: expected int/],
      [{ limit: '10' }, /^limit: Invalid input: expected number, received string$/],
      [{ offset: -1 }, /^offset: Too small/],
      [{ ids: [] }, /^ids: Too small/],
      [{ ids: ids(501) }, /^ids: Too big/],
      [{ types: 'line' }, /^types: Invalid input: expected array, received string$/],
      [{ types: [] }, /^types: Too small/],
      [{ layers: [''] }, /^layers\[0\]: Too small/],
      [{ within: { min: P } }, /^within\.max: is required \(expected object\)$/],
      [{ within: { min: P, max: { x: 1 } } }, /^within\.max\.y: is required \(expected number\)$/],
      [{ unit: 'yd' }, /^unit: Invalid option/],
      [{ filter: 'x' }, /^Unknown argument "filter"$/],
    ],
  },

  get_selected_objects: {
    valid: [{}, { limit: 10, unit: 'm' }],
    invalid: [
      [{ limit: 500 }, /^limit: Too big/],
      [{ unit: 'feet' }, /^unit: Invalid option/],
      [{ ids: IDS }, /^Unknown argument "ids"$/],
      [{ limit: Number.NaN }, /^limit: Invalid input: expected number, received NaN$/],
    ],
  },

  find_objects: {
    valid: [
      {},
      { query: 'kitchen' },
      { query: 'door', types: ['door'], layers: ['Doors'], near: { point: P, radius: 5 }, limit: 20, unit: 'in' },
    ],
    invalid: [
      [{ query: '' }, /^query: Too small/],
      [{ query: 'x'.repeat(201) }, /^query: Too big/],
      [{ query: 5 }, /^query: Invalid input: expected string, received number$/],
      [{ near: { point: P, radius: 0 } }, /^near\.radius: Too small/],
      [{ near: { point: P } }, /^near\.radius: is required/],
      [{ near: { point: { x: 'a', y: 0 }, radius: 1 } }, /^near\.point\.x: Invalid input: expected number, received string$/],
      [{ near: { point: P, radius: 1, extra: true } }, /^near: Unknown argument "extra"$/],
      [{ limit: -3 }, /^limit: Too small/],
      [{ offset: 3 }, /^Unknown argument "offset"$/],
    ],
  },

  inspect_room: {
    valid: [{ roomId: 'r1' }, { name: 'Kitchen' }, { roomId: 'r1', name: 'Kitchen', unit: 'ft' }],
    invalid: [
      [{}, /^Provide "roomId" or "name"/],
      [{ unit: 'ft' }, /^Provide "roomId" or "name"/],
      [{ roomId: '' }, /^roomId: Too small/],
      [{ name: '' }, /^name: Too small/],
      [{ roomId: 12 }, /^roomId: Invalid input: expected string, received number$/],
      [{ roomId: 'r1', id: 'r1' }, /^Unknown argument "id"$/],
    ],
  },

  create_line: {
    valid: [
      { start: P, end: Q },
      { start: P, end: Q, layer: 'Walls', color: '#FF00aa', unit: 'mm' },
      { start: { x: -1e9, y: 0.000001 }, end: Q, color: 'bylayer' },
    ],
    invalid: [
      [{}, /^start: is required \(expected object\)$/],
      [{ start: P }, /^end: is required \(expected object\)$/],
      [{ start: P, end: { x: 1 } }, /^end\.y: is required \(expected number\)$/],
      [{ start: P, end: { x: 1, y: '2' } }, /^end\.y: Invalid input: expected number, received string$/],
      [{ start: P, end: { x: 1, y: Number.POSITIVE_INFINITY } }, /^end\.y: Invalid input: expected number, received Infinity$/],
      [{ start: P, end: { x: 1, y: 2, z: 3 } }, /^end: Unknown argument "z"$/],
      [{ start: [0, 0], end: Q }, /^start: Invalid input: expected object, received array$/],
      [{ start: P, end: Q, color: 'red' }, /^color: must be a "#rrggbb" hex colour/],
      [{ start: P, end: Q, color: '#fff' }, /^color: must be a "#rrggbb" hex colour/],
      [{ start: P, end: Q, layer: '' }, /^layer: Too small/],
      [{ start: P, end: Q, layer: 'x'.repeat(256) }, /^layer: Too big/],
      [{ start: P, end: Q, unit: 'MM' }, /^unit: Invalid option/],
      [{ start: P, end: Q, thickness: 2 }, /^Unknown argument "thickness"$/],
      [{ start: P, end: Q, a: 1, b: 2 }, /^Unknown arguments "a", "b"$/],
    ],
  },

  create_polyline: {
    valid: [
      { points: [P, Q] },
      { points: points(2000), closed: true, layer: 'Outline', color: '#000000', unit: 'cm' },
    ],
    invalid: [
      [{}, /^points: is required \(expected array\)$/],
      [{ points: [P] }, /^points: Too small/],
      [{ points: points(2001) }, /^points: Too big/],
      [{ points: [P, { x: 1 }] }, /^points\[1\]\.y: is required/],
      [{ points: [P, [1, 2]] }, /^points\[1\]: Invalid input: expected object, received array$/],
      [{ points: [P, Q], closed: 'yes' }, /^closed: Invalid input: expected boolean, received string$/],
      [{ points: [P, Q], close: true }, /^Unknown argument "close"$/],
    ],
  },

  create_rectangle: {
    valid: [
      { corner: P, width: 12, height: 14 },
      { corner: Q, width: 0.001, height: 1e6, rotation: -45, layer: 'A', color: 'bylayer', unit: 'ft' },
    ],
    invalid: [
      [{ corner: P, width: 12 }, /^height: is required \(expected number\)$/],
      [{ corner: P, width: 0, height: 1 }, /^width: Too small: expected number to be >0$/],
      [{ corner: P, width: 1, height: -2 }, /^height: Too small/],
      [{ corner: P, width: '12', height: 14 }, /^width: Invalid input: expected number, received string$/],
      [{ corner: P, width: 1, height: 1, rotation: Number.NaN }, /^rotation: Invalid input: expected number, received NaN$/],
      [{ origin: P, width: 1, height: 1 }, /^corner: is required/],
      [{ origin: P, width: 1, height: 1 }, /^Unknown argument "origin"$/],
    ],
  },

  create_circle: {
    valid: [
      { center: P, radius: 5 },
      { center: P, radius: 0.5, layer: 'Fixtures', color: '#123abc', unit: 'in' },
    ],
    invalid: [
      [{ center: P }, /^radius: is required/],
      [{ center: P, radius: 0 }, /^radius: Too small/],
      [{ center: P, radius: -1 }, /^radius: Too small/],
      [{ center: P, diameter: 5 }, /^Unknown argument "diameter"$/],
      [{ center: null, radius: 1 }, /^center: Invalid input: expected object, received null$/],
    ],
  },

  create_arc: {
    valid: [
      { center: P, radius: 5, startAngle: 0, endAngle: 90 },
      { center: P, radius: 5, startAngle: -30, endAngle: 720, layer: 'A', color: '#ffffff', unit: 'm' },
    ],
    invalid: [
      [{ center: P, radius: 5, startAngle: 0 }, /^endAngle: is required/],
      [{ center: P, radius: 0, startAngle: 0, endAngle: 90 }, /^radius: Too small/],
      [{ center: P, radius: 5, startAngle: '0', endAngle: 90 }, /^startAngle: Invalid input/],
      [{ center: P, radius: 5, startAngle: 0, endAngle: 90, clockwise: true }, /^Unknown argument "clockwise"$/],
    ],
  },

  create_text: {
    valid: [
      { position: P, text: 'Kitchen', height: 0.5 },
      { position: P, text: 'x'.repeat(2000), height: 250, rotation: 90, layer: 'Text', color: '#00ff00', unit: 'mm' },
    ],
    invalid: [
      [{ position: P, text: '', height: 1 }, /^text: Too small/],
      [{ position: P, text: 'x'.repeat(2001), height: 1 }, /^text: Too big/],
      [{ position: P, text: 'a', height: 0 }, /^height: Too small/],
      [{ position: P, text: 12, height: 1 }, /^text: Invalid input: expected string, received number$/],
      [{ position: P, height: 1 }, /^text: is required/],
      [{ position: P, text: 'a', height: 1, font: 'Arial' }, /^Unknown argument "font"$/],
    ],
  },

  create_entities: {
    valid: [
      { entities: [{ type: 'line', start: P, end: Q }] },
      {
        entities: [
          { type: 'line', start: P, end: Q, layer: 'A', color: '#ff0000' },
          { type: 'polyline', points: [P, Q, { x: 3, y: 9 }], closed: true },
          { type: 'rectangle', corner: P, width: 3, height: 2, rotation: 30 },
          { type: 'circle', center: Q, radius: 1.5, color: 'bylayer' },
          { type: 'arc', center: P, radius: 2, startAngle: 0, endAngle: 180 },
          { type: 'text', position: P, text: 'Hall', height: 0.4, rotation: 0 },
        ],
        layer: 'Furniture',
        unit: 'ft',
      },
      { entities: Array.from({ length: 500 }, () => ({ type: 'circle', center: P, radius: 1 })) },
    ],
    invalid: [
      [{}, /^entities: is required \(expected array\)$/],
      [{ entities: [] }, /^entities: Too small/],
      [
        { entities: Array.from({ length: 501 }, () => ({ type: 'circle', center: P, radius: 1 })) },
        /^entities: Too big/,
      ],
      [{ entities: [{ type: 'ellipse', center: P }] }, /^entities\[0\]\.type: /],
      [{ entities: [{ start: P, end: Q }] }, /^entities\[0\]\.type: /],
      [{ entities: [{ type: 'line', start: P }] }, /^entities\[0\]\.end: is required/],
      [{ entities: [{ type: 'line', start: P, end: Q, unit: 'mm' }] }, /^entities\[0\]: Unknown argument "unit"$/],
      [{ entities: [{ type: 'circle', center: P, radius: 1 }, { type: 'circle', center: P, radius: 0 }] }, /^entities\[1\]\.radius: Too small/],
      [{ entities: [{ type: 'rectangle', corner: P, width: 1, height: 1, radius: 3 }] }, /^entities\[0\]: Unknown argument "radius"$/],
      [{ entities: [{ type: 'line', start: P, end: Q }], color: '#ffffff' }, /^Unknown argument "color"$/],
      [{ entities: 'line' }, /^entities: Invalid input: expected array, received string$/],
    ],
  },

  create_wall: {
    valid: [
      { start: P, end: Q, thickness: 0.75 },
      { start: P, end: Q, thickness: 230, kind: 'external', justification: 'left', height: 3000, unit: 'mm' },
    ],
    invalid: [
      [{ start: P, end: Q }, /^thickness: is required/],
      [{ start: P, end: Q, thickness: 0 }, /^thickness: Too small/],
      [{ start: P, end: Q, thickness: 1, kind: 'load_bearing' }, /^kind: Invalid option/],
      [{ start: P, end: Q, thickness: 1, justification: 'middle' }, /^justification: Invalid option/],
      [{ start: P, end: Q, thickness: 1, height: -1 }, /^height: Too small/],
      [{ start: P, end: Q, thickness: 1, layer: 'Walls' }, /^Unknown argument "layer"$/],
    ],
  },

  create_room: {
    valid: [
      { name: 'Bedroom', width: 12, length: 14 },
      {
        name: 'Master Bedroom',
        roomType: 'master_bedroom',
        width: 12,
        length: 14,
        origin: P,
        wallThickness: 0.75,
        createWalls: false,
        addLabel: true,
        unit: 'ft',
      },
    ],
    invalid: [
      [{ width: 12, length: 14 }, /^name: is required \(expected string\)$/],
      [{ name: '', width: 12, length: 14 }, /^name: Too small/],
      [{ name: 'x'.repeat(101), width: 12, length: 14 }, /^name: Too big/],
      [{ name: 'A', width: 12 }, /^length: is required/],
      [{ name: 'A', width: 0, length: 14 }, /^width: Too small/],
      [{ name: 'A', width: 12, length: 14, roomType: 'bed room' }, /^roomType: Invalid option/],
      [{ name: 'A', width: 12, length: 14, origin: { x: 0 } }, /^origin\.y: is required/],
      [{ name: 'A', width: 12, length: 14, createWalls: 1 }, /^createWalls: Invalid input: expected boolean, received number$/],
      [{ name: 'A', width: 12, length: 14, height: 10 }, /^Unknown argument "height"$/],
    ],
  },

  create_door: {
    valid: [
      { position: P, width: 3 },
      { position: P, width: 900, wallId: 'w1', doorType: 'main_entrance', swing: 'right', opensInto: 'outside', unit: 'mm' },
    ],
    invalid: [
      [{ position: P }, /^width: is required/],
      [{ position: P, width: 0 }, /^width: Too small/],
      [{ position: P, width: 3, doorType: 'revolving' }, /^doorType: Invalid option/],
      [{ position: P, width: 3, swing: 'up' }, /^swing: Invalid option/],
      [{ position: P, width: 3, opensInto: 'room' }, /^opensInto: Invalid option/],
      [{ position: P, width: 3, wallId: '' }, /^wallId: Too small/],
      [{ position: P, width: 3, height: 7 }, /^Unknown argument "height"$/],
    ],
  },

  create_window: {
    valid: [
      { position: P, width: 4 },
      { position: P, width: 1200, wallId: 'w1', windowType: 'casement', sillHeight: 0, height: 1200, unit: 'mm' },
    ],
    invalid: [
      [{ width: 4 }, /^position: is required/],
      [{ position: P, width: -4 }, /^width: Too small/],
      [{ position: P, width: 4, windowType: 'bay' }, /^windowType: Invalid option/],
      [{ position: P, width: 4, sillHeight: -0.1 }, /^sillHeight: Too small/],
      [{ position: P, width: 4, height: 0 }, /^height: Too small/],
      [{ position: P, width: 4, swing: 'left' }, /^Unknown argument "swing"$/],
    ],
  },

  create_staircase: {
    valid: [
      { origin: P, width: 3, length: 10 },
      { origin: P, width: 1000, length: 3000, direction: 180, steps: 12, unit: 'mm' },
      { origin: P, width: 3, length: 10, steps: 2 },
      { origin: P, width: 3, length: 10, steps: 60 },
    ],
    invalid: [
      [{ origin: P, width: 3 }, /^length: is required/],
      [{ origin: P, width: 3, length: 10, steps: 1 }, /^steps: Too small/],
      [{ origin: P, width: 3, length: 10, steps: 61 }, /^steps: Too big/],
      [{ origin: P, width: 3, length: 10, steps: 10.5 }, /^steps: Invalid input: expected int/],
      [{ origin: P, width: 3, length: 10, direction: 'north' }, /^direction: Invalid input: expected number, received string$/],
      [{ origin: P, width: 3, length: 10, rise: 7 }, /^Unknown argument "rise"$/],
    ],
  },

  create_column: {
    valid: [
      { center: P, shape: 'rectangular', width: 1, depth: 1.5 },
      { center: P, shape: 'rectangular', width: 300, depth: 450, rotation: 45, unit: 'mm' },
      { center: P, shape: 'circular', diameter: 1 },
      { center: P, shape: 'circular', diameter: 400, unit: 'mm' },
    ],
    invalid: [
      [{ center: P }, /^shape: Invalid option/],
      [{ center: P, shape: 'square', width: 1, depth: 1 }, /^shape: Invalid option/],
      [{ center: P, shape: 'rectangular' }, /^width: A rectangular column needs "width"$/],
      [{ center: P, shape: 'rectangular', width: 1 }, /^depth: A rectangular column needs "depth"$/],
      [{ center: P, shape: 'rectangular', width: 1, depth: 1, diameter: 2 }, /^diameter: A rectangular column takes "width" and "depth", not "diameter"$/],
      [{ center: P, shape: 'circular' }, /^diameter: A circular column needs "diameter"$/],
      [{ center: P, shape: 'circular', width: 1, depth: 1 }, /^diameter: A circular column needs "diameter"$/],
      [{ center: P, shape: 'circular', diameter: 1, width: 1 }, /^width: A circular column takes "diameter", not "width"$/],
      [{ center: P, shape: 'circular', diameter: 1, depth: 1 }, /^depth: A circular column takes "diameter", not "depth"$/],
      [{ center: P, shape: 'circular', diameter: 0 }, /^diameter: Too small/],
      [{ center: P, shape: 'circular', diameter: 1, radius: 1 }, /^Unknown argument "radius"$/],
    ],
  },

  create_plot_boundary: {
    valid: [
      { width: 30, length: 40 },
      { width: 30, length: 40, origin: P, setbacks: { front: 5, back: 3, left: 0, right: 2.5 }, unit: 'ft' },
      { width: 30, length: 40, setbacks: {} },
    ],
    invalid: [
      [{ width: 30 }, /^length: is required/],
      [{ width: 0, length: 40 }, /^width: Too small/],
      [{ width: 30, length: 40, setbacks: { front: -1 } }, /^setbacks\.front: Too small/],
      [{ width: 30, length: 40, setbacks: { top: 1 } }, /^setbacks: Unknown argument "top"$/],
      [{ width: 30, length: 40, setbacks: 5 }, /^setbacks: Invalid input: expected object, received number$/],
      [{ width: 30, length: 40, height: 1 }, /^Unknown argument "height"$/],
    ],
  },

  create_floor_plan: {
    valid: [
      { plot: { width: 30, length: 40 }, rooms: [{ name: 'Living', roomType: 'living_room' }] },
      {
        plot: { width: 30, length: 40 },
        rooms: [
          { name: 'Living', roomType: 'living_room', width: 14, length: 16 },
          { name: 'Kitchen', roomType: 'kitchen', minArea: 80, adjacentTo: ['Living'] },
          { name: 'Bedroom 1', roomType: 'bedroom', minArea: 120, adjacentTo: [] },
        ],
        entranceSide: 'south',
        externalWallThickness: 0.75,
        internalWallThickness: 0.375,
        previewOnly: true,
        unit: 'ft',
      },
    ],
    invalid: [
      [{ rooms: [{ name: 'Living', roomType: 'living_room' }] }, /^plot: is required \(expected object\)$/],
      [{ plot: { width: 30, length: 40 } }, /^rooms: is required \(expected array\)$/],
      [{ plot: { width: 30, length: 40 }, rooms: [] }, /^rooms: Too small/],
      [
        {
          plot: { width: 30, length: 40 },
          rooms: Array.from({ length: 31 }, (_, index) => ({ name: `R${index}`, roomType: 'other' })),
        },
        /^rooms: Too big/,
      ],
      [{ plot: { width: 30 }, rooms: [{ name: 'A', roomType: 'other' }] }, /^plot\.length: is required/],
      [{ plot: { width: 30, length: 40, origin: P }, rooms: [{ name: 'A', roomType: 'other' }] }, /^plot: Unknown argument "origin"$/],
      [{ plot: { width: 30, length: 40 }, rooms: [{ name: 'A' }] }, /^rooms\[0\]\.roomType: Invalid option/],
      [{ plot: { width: 30, length: 40 }, rooms: [{ name: 'A', roomType: 'other', minArea: 0 }] }, /^rooms\[0\]\.minArea: Too small/],
      [{ plot: { width: 30, length: 40 }, rooms: [{ name: 'A', roomType: 'other', adjacentTo: 'B' }] }, /^rooms\[0\]\.adjacentTo: Invalid input: expected array/],
      [{ plot: { width: 30, length: 40 }, rooms: [{ name: 'A', roomType: 'other', area: 9 }] }, /^rooms\[0\]: Unknown argument "area"$/],
      [{ plot: { width: 30, length: 40 }, rooms: [{ name: 'A', roomType: 'other' }], entranceSide: 'front' }, /^entranceSide: Invalid option/],
      [{ plot: { width: 30, length: 40 }, rooms: [{ name: 'A', roomType: 'other' }], previewOnly: 'true' }, /^previewOnly: Invalid input: expected boolean/],
    ],
  },

  move_entities: {
    valid: [
      { ids: ['a'], dx: 3, dy: 0 },
      { ids: IDS, dx: -1.5, dy: 2, unit: 'ft' },
      { ids: ids(500), dx: 0, dy: 0 },
    ],
    invalid: [
      [{ ids: IDS, dx: 3 }, /^dy: is required \(expected number\)$/],
      [{ dx: 1, dy: 1 }, /^ids: is required \(expected array\)$/],
      [{ ids: [], dx: 1, dy: 1 }, /^ids: Too small/],
      [{ ids: ids(501), dx: 1, dy: 1 }, /^ids: Too big/],
      [{ ids: IDS, dx: '3', dy: 0 }, /^dx: Invalid input: expected number, received string$/],
      [{ ids: IDS, dx: Number.NEGATIVE_INFINITY, dy: 0 }, /^dx: Invalid input/],
      [{ ids: IDS, dx: 1, dy: 1, dz: 1 }, /^Unknown argument "dz"$/],
    ],
  },

  copy_entities: {
    valid: [
      { ids: IDS, dx: 3, dy: 0 },
      { ids: IDS, dx: 3, dy: 0, count: 100, unit: 'm' },
    ],
    invalid: [
      [{ ids: IDS, dx: 3, dy: 0, count: 0 }, /^count: Too small/],
      [{ ids: IDS, dx: 3, dy: 0, count: 101 }, /^count: Too big/],
      [{ ids: IDS, dx: 3, dy: 0, count: 1.5 }, /^count: Invalid input: expected int/],
      [{ ids: IDS, dy: 0 }, /^dx: is required/],
      [{ ids: IDS, dx: 3, dy: 0, copies: 2 }, /^Unknown argument "copies"$/],
    ],
  },

  rotate_entities: {
    valid: [
      { ids: IDS, center: P, angle: 90 },
      { ids: IDS, center: Q, angle: -270.5, unit: 'cm' },
    ],
    invalid: [
      [{ ids: IDS, center: P }, /^angle: is required/],
      [{ ids: IDS, angle: 90 }, /^center: is required/],
      [{ ids: IDS, center: P, angle: '90deg' }, /^angle: Invalid input: expected number, received string$/],
      [{ ids: IDS, center: P, angle: 90, radians: true }, /^Unknown argument "radians"$/],
    ],
  },

  scale_entities: {
    valid: [
      { ids: IDS, center: P, factor: 2 },
      { ids: IDS, center: P, factor: 0.01, unit: 'in' },
    ],
    invalid: [
      [{ ids: IDS, center: P, factor: 0 }, /^factor: Too small: expected number to be >0$/],
      [{ ids: IDS, center: P, factor: -2 }, /^factor: Too small/],
      [{ ids: IDS, center: P }, /^factor: is required/],
      [{ ids: IDS, center: P, factor: 2, factorY: 3 }, /^Unknown argument "factorY"$/],
    ],
  },

  mirror_entities: {
    valid: [
      { ids: IDS, lineStart: P, lineEnd: Q },
      { ids: IDS, lineStart: P, lineEnd: Q, keepOriginal: true, unit: 'ft' },
    ],
    invalid: [
      [{ ids: IDS, lineStart: P }, /^lineEnd: is required/],
      [{ ids: IDS, lineStart: P, lineEnd: Q, keepOriginal: 'no' }, /^keepOriginal: Invalid input: expected boolean/],
      [{ ids: IDS, lineStart: P, lineEnd: { x: 1, y: null } }, /^lineEnd\.y: Invalid input: expected number, received null$/],
      [{ ids: IDS, lineStart: P, lineEnd: Q, axis: 'x' }, /^Unknown argument "axis"$/],
    ],
  },

  update_entity_properties: {
    valid: [
      { ids: IDS, layer: 'Walls' },
      { ids: IDS, color: '#ff0000' },
      { ids: IDS, layer: 'Walls', color: 'bylayer' },
    ],
    invalid: [
      [{ ids: IDS }, /^Provide at least one of "layer" and "color"$/],
      [{ layer: 'Walls' }, /^ids: is required/],
      [{ ids: IDS, color: 'blue' }, /^color: must be a "#rrggbb" hex colour/],
      [{ ids: IDS, layer: '' }, /^layer: Too small/],
      [{ ids: IDS, layer: 'A', lineweight: 2 }, /^Unknown argument "lineweight"$/],
      [{ ids: IDS, layer: 'A', unit: 'mm' }, /^Unknown argument "unit"$/],
    ],
  },

  move_room: {
    valid: [
      { roomId: 'r1', dx: 3, dy: 0 },
      { name: 'Kitchen', dx: 0, dy: -2, unit: 'ft' },
      { roomId: 'r1', name: 'Kitchen', dx: 1, dy: 1 },
    ],
    invalid: [
      [{ dx: 3, dy: 0 }, /^Provide "roomId" or "name"/],
      [{ roomId: 'r1', dx: 3 }, /^dy: is required/],
      [{ roomId: '', dx: 3, dy: 0 }, /^roomId: Too small/],
      [{ roomId: 'r1', dx: 3, dy: 0, ids: IDS }, /^Unknown argument "ids"$/],
    ],
  },

  resize_room: {
    valid: [
      { roomId: 'r1', width: 12 },
      { name: 'Kitchen', length: 10 },
      { roomId: 'r1', name: 'Kitchen', width: 12, length: 14, anchor: 'center', unit: 'ft' },
    ],
    invalid: [
      [{ width: 12 }, /^Provide "roomId" or "name"/],
      [{ roomId: 'r1' }, /^Provide at least one of "width" and "length"$/],
      [{ roomId: 'r1', anchor: 'ne' }, /^Provide at least one of "width" and "length"$/],
      [{}, /^Provide "roomId" or "name"/],
      [{}, /^Provide at least one of "width" and "length"$/],
      [{ roomId: 'r1', width: 0 }, /^width: Too small/],
      [{ roomId: 'r1', width: 12, anchor: 'middle' }, /^anchor: Invalid option/],
      [{ roomId: 'r1', width: 12, area: 100 }, /^Unknown argument "area"$/],
    ],
  },

  delete_entities: ID_LIST_ONLY,
  zoom_to_entities: ID_LIST_ONLY,
  highlight_entities: ID_LIST_ONLY,

  add_dimension: {
    valid: [
      { kind: 'linear', from: P, to: Q, offset: 2 },
      { kind: 'aligned', from: P, to: Q, offset: -500, unit: 'mm' },
      { kind: 'linear', from: P, to: Q, offset: 0 },
    ],
    invalid: [
      [{ from: P, to: Q, offset: 2 }, /^kind: Invalid option/],
      [{ kind: 'angular', from: P, to: Q, offset: 2 }, /^kind: Invalid option/],
      [{ kind: 'linear', from: P, to: Q }, /^offset: is required/],
      [{ kind: 'linear', from: P, offset: 1 }, /^to: is required/],
      [{ kind: 'linear', from: P, to: Q, offset: 1, text: '10' }, /^Unknown argument "text"$/],
    ],
  },

  add_room_labels: {
    valid: [{}, { roomIds: ['r1', 'r2'], showArea: false, textHeight: 0.5, unit: 'ft' }],
    invalid: [
      [{ roomIds: [] }, /^roomIds: Too small/],
      [{ textHeight: 0 }, /^textHeight: Too small/],
      [{ showArea: 'yes' }, /^showArea: Invalid input: expected boolean/],
      [{ rooms: ['r1'] }, /^Unknown argument "rooms"$/],
    ],
  },

  create_layer: {
    valid: [{ name: 'Walls' }, { name: 'x'.repeat(255), color: '#FFCC00', setCurrent: true }],
    invalid: [
      [{}, /^name: is required \(expected string\)$/],
      [{ name: '' }, /^name: Too small/],
      [{ name: 'x'.repeat(256) }, /^name: Too big/],
      [{ name: 'Walls', color: 'bylayer' }, /^color: must be a "#rrggbb" hex colour/],
      [{ name: 'Walls', color: 'ffcc00' }, /^color: must be a "#rrggbb" hex colour/],
      [{ name: 'Walls', setCurrent: 'true' }, /^setCurrent: Invalid input: expected boolean/],
      [{ name: 'Walls', visible: true }, /^Unknown argument "visible"$/],
      [{ name: 7 }, /^name: Invalid input: expected string, received number$/],
    ],
  },

  set_current_layer: {
    valid: [{ name: 'Walls' }, { name: '0' }],
    invalid: [
      [{}, /^name: is required/],
      [{ name: '' }, /^name: Too small/],
      [{ layer: 'Walls' }, /^Unknown argument "layer"$/],
    ],
  },

  set_layer_state: {
    valid: [
      { name: 'Walls', visible: false },
      { name: 'Walls', locked: true },
      { name: 'Walls', color: '#00ff00' },
      { name: 'Walls', newName: 'Outer walls' },
      { name: 'Walls', visible: true, locked: false, color: '#112233', newName: 'W' },
    ],
    invalid: [
      [{ name: 'Walls' }, /^Provide at least one change/],
      [{ visible: true }, /^name: is required/],
      [{ name: 'Walls', color: 'bylayer' }, /^color: must be a "#rrggbb" hex colour/],
      [{ name: 'Walls', newName: '' }, /^newName: Too small/],
      [{ name: 'Walls', visible: 0 }, /^visible: Invalid input: expected boolean, received number$/],
      [{ name: 'Walls', frozen: true }, /^Unknown argument "frozen"$/],
    ],
  },

  validate_drawing: {
    valid: [{}, { checks: ['overlaps', 'zero_length'] }, undefined],
    invalid: [
      [{ checks: [] }, /^checks: Too small/],
      [{ checks: 'overlaps' }, /^checks: Invalid input: expected array, received string$/],
      [{ checks: [''] }, /^checks\[0\]: Too small/],
      [{ fix: true }, /^Unknown argument "fix"$/],
    ],
  },
};

describe('validateToolArguments', () => {
  it('has an accept/reject table for every tool', () => {
    expect(Object.keys(CASES).sort()).toEqual([...TOOL_NAMES].sort());
    for (const name of TOOL_NAMES) {
      expect(CASES[name].valid.length, name).toBeGreaterThan(0);
      expect(CASES[name].invalid.length, name).toBeGreaterThan(1);
    }
  });

  describe.each(TOOL_NAMES.map((name) => [name] as const))('%s', (name) => {
    it('accepts its valid examples', () => {
      for (const args of CASES[name].valid) {
        const result = validateToolArguments(name, args);
        expect(result, JSON.stringify(args)?.slice(0, 200)).toMatchObject({ ok: true });
      }
    });

    it('rejects its invalid examples with readable errors', () => {
      for (const [args, pattern] of CASES[name].invalid) {
        const result = validateToolArguments(name, args);
        const label = JSON.stringify(args)?.slice(0, 200);
        expect(result.ok, label).toBe(false);
        if (result.ok) continue;
        expect(result.errors.length, label).toBeGreaterThan(0);
        expect(result.errors.length, label).toBeLessThanOrEqual(MAX_VALIDATION_ERRORS);
        expect(
          result.errors.some((error) => pattern.test(error)),
          `${label} -> ${JSON.stringify(result.errors)} should match ${String(pattern)}`,
        ).toBe(true);
      }
    });

    it('rejects an unknown top-level key and non-object arguments', () => {
      const base = CASES[name].valid[0];
      const withExtra = { ...(typeof base === 'object' && base !== null ? base : {}), __unexpected: 1 };
      const result = validateToolArguments(name, withExtra);
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.errors).toContain('Unknown argument "__unexpected"');
      expect(validateToolArguments(name, 'nope').ok).toBe(false);
      expect(validateToolArguments(name, 12).ok).toBe(false);
      expect(validateToolArguments(name, [1, 2]).ok).toBe(false);
    });
  });

  it('reports an unknown tool', () => {
    expect(validateToolArguments('explode_everything', {})).toEqual({
      ok: false,
      errors: ['Unknown tool "explode_everything"'],
    });
    expect(validateToolArguments('', {})).toEqual({ ok: false, errors: ['Unknown tool ""'] });
    expect(validateToolArguments('hasOwnProperty', {})).toEqual({
      ok: false,
      errors: ['Unknown tool "hasOwnProperty"'],
    });
  });

  it('returns the parsed value with defaults applied', () => {
    expect(validateToolArguments('inspect_entities', {})).toEqual({ ok: true, value: { limit: 50, offset: 0 } });
    expect(validateToolArguments('inspect_entities', { limit: 5 })).toEqual({
      ok: true,
      value: { limit: 5, offset: 0 },
    });
    expect(validateToolArguments('get_selected_objects', undefined)).toEqual({ ok: true, value: { limit: 50 } });
    expect(validateToolArguments('find_objects', { query: 'door' })).toEqual({
      ok: true,
      value: { query: 'door', limit: 50 },
    });
    expect(validateToolArguments('copy_entities', { ids: ['a'], dx: 1, dy: 2 })).toEqual({
      ok: true,
      value: { ids: ['a'], dx: 1, dy: 2, count: 1 },
    });
    expect(validateToolArguments('create_staircase', { origin: P, width: 3, length: 10 })).toEqual({
      ok: true,
      value: { origin: P, width: 3, length: 10, direction: 90 },
    });
    expect(validateToolArguments('create_line', { start: P, end: Q })).toEqual({
      ok: true,
      value: { start: P, end: Q },
    });
  });

  it('is idempotent: a parsed value validates to itself', () => {
    for (const name of TOOL_NAMES) {
      for (const args of CASES[name].valid) {
        const first = validateToolArguments(name, args);
        expect(first.ok).toBe(true);
        if (!first.ok) continue;
        expect(validateToolArguments(name, first.value)).toEqual(first);
      }
    }
  });

  it('does not mutate the arguments it is given', () => {
    const args = { ids: ['a'], dx: 1, dy: 2 };
    const snapshot = JSON.parse(JSON.stringify(args)) as unknown;
    const result = validateToolArguments('copy_entities', args);
    expect(args).toEqual(snapshot);
    expect(result.ok && result.value).not.toBe(args);
  });

  it('types the value for known tool names', () => {
    const result = validateToolArguments('create_rectangle', { corner: P, width: 12, height: 14, unit: 'ft' });
    expect(result.ok).toBe(true);
    if (result.ok) {
      const area: number = result.value.width * result.value.height;
      expect(area).toBe(168);
      expect(result.value.unit).toBe('ft');
    }
  });

  it('reports every problem of a call at once', () => {
    const result = validateToolArguments('create_rectangle', { corner: { x: 'a' }, width: -1, colour: 'red' });
    expect(result).toEqual({
      ok: false,
      errors: [
        'corner.x: Invalid input: expected number, received string',
        'corner.y: is required (expected number)',
        'width: Too small: expected number to be >0',
        'height: is required (expected number)',
        'Unknown argument "colour"',
      ],
    });
  });

  it('caps the number of error strings at 20', () => {
    const result = validateToolArguments('create_polyline', {
      points: Array.from({ length: 60 }, () => ({ x: 'bad', y: 0 })),
    });
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors).toHaveLength(MAX_VALIDATION_ERRORS);
      expect(result.errors[0]).toBe('points[0].x: Invalid input: expected number, received string');
      expect(result.errors[19]).toBe('... and 41 more problems');
    }
  });

  it('checks the cross-field rules only through validateToolArguments, with clear messages', () => {
    expect(TOOLS.update_entity_properties.input.safeParse({ ids: ['a'] }).success).toBe(false);
    const column = validateToolArguments('create_column', { center: P, shape: 'rectangular', diameter: 3 });
    expect(column).toEqual({
      ok: false,
      errors: [
        'width: A rectangular column needs "width"',
        'depth: A rectangular column needs "depth"',
        'diameter: A rectangular column takes "width" and "depth", not "diameter"',
      ],
    });
  });

  it('rejects non-finite numbers everywhere', () => {
    for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]) {
      expect(validateToolArguments('create_circle', { center: P, radius: bad }).ok).toBe(false);
      expect(validateToolArguments('create_circle', { center: { x: bad, y: 0 }, radius: 1 }).ok).toBe(false);
      expect(validateToolArguments('move_entities', { ids: ['a'], dx: bad, dy: 0 }).ok).toBe(false);
      expect(validateToolArguments('rotate_entities', { ids: ['a'], center: P, angle: bad }).ok).toBe(false);
      expect(validateToolArguments('add_dimension', { kind: 'linear', from: P, to: Q, offset: bad }).ok).toBe(false);
    }
  });
});
