// One-line human summaries of tool calls, for approval cards and the activity log. Browser-safe.
import { UNITS } from './core';
import { getTool } from './tools/catalog';
import { isToolName } from './tools/names';
import type { ToolName } from './tools/names';

type Args = Record<string, unknown>;

const MAX_LINE_LENGTH = 140;
const MAX_QUOTED_LENGTH = 40;

function isRecord(value: unknown): value is Args {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function num(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function str(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value : undefined;
}

/** Formats a number with at most 3 decimals and no trailing zeros. */
function fmt(value: number): string {
  const rounded = Math.round(value * 1000) / 1000;
  return String(Object.is(rounded, -0) ? 0 : rounded);
}

function shorten(text: string, max: number): string {
  const singleLine = text.replace(/\s+/g, ' ').trim();
  return singleLine.length > max ? `${singleLine.slice(0, max - 3)}...` : singleLine;
}

function quote(text: string): string {
  return `"${shorten(text, MAX_QUOTED_LENGTH)}"`;
}

function pt(value: unknown): string | undefined {
  if (!isRecord(value)) return undefined;
  const x = num(value.x);
  const y = num(value.y);
  return x === undefined || y === undefined ? undefined : `(${fmt(x)}, ${fmt(y)})`;
}

/** ' ft' when the call names a valid unit, otherwise '' (the drawing unit applies). */
function unitSuffix(args: Args): string {
  const unit = args.unit;
  return typeof unit === 'string' && (UNITS as readonly string[]).includes(unit) ? ` ${unit}` : '';
}

function plural(count: number, singular: string, pluralForm = `${singular}s`): string {
  return `${count} ${count === 1 ? singular : pluralForm}`;
}

/** '3 objects' for an id list; 'objects' when the list is missing or malformed. */
function objects(value: unknown, singular = 'object'): string {
  return Array.isArray(value) ? plural(value.length, singular) : `${singular}s`;
}

/** ' 12 x 14 ft' from two sizes; '' when either is missing. */
function size(a: unknown, b: unknown, args: Args): string {
  const first = num(a);
  const second = num(b);
  return first === undefined || second === undefined ? '' : ` ${fmt(first)} x ${fmt(second)}${unitSuffix(args)}`;
}

function at(value: unknown): string {
  const text = pt(value);
  return text === undefined ? '' : ` at ${text}`;
}

function fromTo(from: unknown, to: unknown, args: Args): string {
  const a = pt(from);
  const b = pt(to);
  return a === undefined || b === undefined ? '' : ` from ${a} to ${b}${unitSuffix(args)}`;
}

function by(args: Args): string {
  const dx = num(args.dx);
  const dy = num(args.dy);
  return dx === undefined || dy === undefined ? '' : ` by (${fmt(dx)}, ${fmt(dy)})${unitSuffix(args)}`;
}

function length(label: string, value: unknown, args: Args): string {
  const n = num(value);
  return n === undefined ? '' : ` ${label} ${fmt(n)}${unitSuffix(args)}`;
}

function wide(value: unknown, args: Args): string {
  const n = num(value);
  return n === undefined ? '' : ` ${fmt(n)}${unitSuffix(args)} wide`;
}

function stringList(value: unknown, max = 3): string | undefined {
  if (!Array.isArray(value)) return undefined;
  const items = value.filter((item): item is string => typeof item === 'string' && item.length > 0);
  if (items.length === 0) return undefined;
  const shown = items.slice(0, max).map((item) => shorten(item, 24));
  return items.length > max ? `${shown.join(', ')} and ${items.length - max} more` : shown.join(', ');
}

function room(args: Args): string {
  const name = str(args.name);
  if (name !== undefined) return `room ${quote(name)}`;
  const roomId = str(args.roomId);
  return roomId !== undefined ? `room ${shorten(roomId, 24)}` : 'room';
}

function filters(args: Args): string {
  let text = '';
  const types = stringList(args.types);
  if (types !== undefined) text += ` of type ${types}`;
  const layers = stringList(args.layers);
  if (layers !== undefined) text += ` on layer ${layers}`;
  return text;
}

function describeBatch(args: Args): string {
  const items = args.entities;
  if (!Array.isArray(items)) return 'Create entities';
  const counts = new Map<string, number>();
  for (const item of items) {
    const type = isRecord(item) ? str(item.type) : undefined;
    if (type !== undefined) counts.set(type, (counts.get(type) ?? 0) + 1);
  }
  const breakdown = [...counts.entries()].map(([type, count]) => plural(count, shorten(type, 16))).join(', ');
  const total = `Create ${plural(items.length, 'object')}`;
  return breakdown.length === 0 || counts.size > 6 ? total : `${total} (${breakdown})`;
}

function describeLayerState(args: Args): string {
  const changes: string[] = [];
  if (args.visible === true) changes.push('show');
  if (args.visible === false) changes.push('hide');
  if (args.locked === true) changes.push('lock');
  if (args.locked === false) changes.push('unlock');
  const color = str(args.color);
  if (color !== undefined) changes.push(`color ${shorten(color, 12)}`);
  const newName = str(args.newName);
  if (newName !== undefined) changes.push(`rename to ${quote(newName)}`);
  const name = str(args.name);
  const target = name !== undefined ? `layer ${quote(name)}` : 'layer';
  return changes.length === 0 ? `Update ${target}` : `Update ${target}: ${changes.join(', ')}`;
}

function describeProperties(args: Args): string {
  const parts: string[] = [];
  const layer = str(args.layer);
  if (layer !== undefined) parts.push(`layer ${quote(layer)}`);
  const color = str(args.color);
  if (color !== undefined) parts.push(`color ${shorten(color, 12)}`);
  const target = objects(args.ids);
  return parts.length === 0 ? `Update properties of ${target}` : `Set ${parts.join(' and ')} on ${target}`;
}

const DESCRIBERS: { readonly [N in ToolName]: (args: Args) => string } = {
  get_drawing_info: () => 'Read drawing info',
  list_layers: () => 'List layers',
  inspect_entities: (a) => {
    if (Array.isArray(a.ids)) return `Inspect ${objects(a.ids)}${filters(a)}`;
    return `Inspect objects${filters(a)}${isRecord(a.within) ? ' in a window' : ''}`;
  },
  get_selected_objects: () => 'Read the selected objects',
  find_objects: (a) => {
    const query = str(a.query);
    const near = isRecord(a.near) ? pt(a.near.point) : undefined;
    return `Find objects${query !== undefined ? ` matching ${quote(query)}` : ''}${filters(a)}${
      near !== undefined ? ` near ${near}` : ''
    }`;
  },
  inspect_room: (a) => `Inspect ${room(a)}`,
  get_drawing_revision: () => 'Check drawing revision',

  create_line: (a) => `Create line${fromTo(a.start, a.end, a)}`,
  create_polyline: (a) => {
    const kind = a.closed === true ? 'closed polyline' : 'polyline';
    return Array.isArray(a.points) ? `Create ${kind} with ${plural(a.points.length, 'point')}` : `Create ${kind}`;
  },
  create_rectangle: (a) => `Create rectangle${size(a.width, a.height, a)}${at(a.corner)}`,
  create_circle: (a) => `Create circle${length('radius', a.radius, a)}${at(a.center)}`,
  create_arc: (a) => {
    const start = num(a.startAngle);
    const end = num(a.endAngle);
    const sweep = start === undefined || end === undefined ? '' : `, ${fmt(start)} to ${fmt(end)} deg`;
    return `Create arc${length('radius', a.radius, a)}${at(a.center)}${sweep}`;
  },
  create_text: (a) => {
    const text = str(a.text);
    return `Create text${text !== undefined ? ` ${quote(text)}` : ''}${at(a.position)}`;
  },
  create_entities: describeBatch,

  create_wall: (a) => {
    const kind = str(a.kind);
    const thickness = num(a.thickness);
    return `Create ${kind !== undefined ? `${shorten(kind, 16)} ` : ''}wall${fromTo(a.start, a.end, a)}${
      thickness !== undefined ? `, ${fmt(thickness)}${unitSuffix(a)} thick` : ''
    }`;
  },
  create_room: (a) => {
    const name = str(a.name);
    return `Create room${name !== undefined ? ` ${quote(name)}` : ''}${size(a.width, a.length, a)}${at(a.origin)}`;
  },
  create_door: (a) => {
    const type = str(a.doorType);
    const label = type === undefined || type === 'single' ? 'door' : `${shorten(type.replace(/_/g, ' '), 20)} door`;
    return `Create ${label}${wide(a.width, a)}${at(a.position)}`;
  },
  create_window: (a) => {
    const type = str(a.windowType);
    return `Create ${type !== undefined ? `${shorten(type, 16)} ` : ''}window${wide(a.width, a)}${at(a.position)}`;
  },
  create_staircase: (a) => {
    const steps = num(a.steps);
    return `Create staircase${size(a.width, a.length, a)}${at(a.origin)}${
      steps !== undefined ? `, ${plural(steps, 'step')}` : ''
    }`;
  },
  create_column: (a) => {
    if (a.shape === 'circular') return `Create circular column${length('diameter', a.diameter, a)}${at(a.center)}`;
    return `Create column${size(a.width, a.depth, a)}${at(a.center)}`;
  },
  create_plot_boundary: (a) => `Create plot boundary${size(a.width, a.length, a)}${at(a.origin)}`,
  create_floor_plan: (a) => {
    const verb = a.previewOnly === true ? 'Preview' : 'Create';
    const rooms = Array.isArray(a.rooms) ? ` with ${plural(a.rooms.length, 'room')}` : '';
    const plot = isRecord(a.plot) ? size(a.plot.width, a.plot.length, a) : '';
    return `${verb} floor plan${rooms}${plot.length > 0 ? ` on a${plot} plot` : ''}`;
  },

  move_entities: (a) => `Move ${objects(a.ids)}${by(a)}`,
  copy_entities: (a) => {
    const count = num(a.count);
    return `Copy ${objects(a.ids)}${by(a)}${count !== undefined && count > 1 ? `, ${fmt(count)} times` : ''}`;
  },
  rotate_entities: (a) => {
    const angle = num(a.angle);
    const center = pt(a.center);
    return `Rotate ${objects(a.ids)}${angle !== undefined ? ` by ${fmt(angle)} deg` : ''}${
      center !== undefined ? ` about ${center}` : ''
    }`;
  },
  scale_entities: (a) => {
    const factor = num(a.factor);
    const center = pt(a.center);
    return `Scale ${objects(a.ids)}${factor !== undefined ? ` by ${fmt(factor)}x` : ''}${
      center !== undefined ? ` about ${center}` : ''
    }`;
  },
  mirror_entities: (a) => {
    const start = pt(a.lineStart);
    const end = pt(a.lineEnd);
    const line = start === undefined || end === undefined ? '' : ` across the line ${start} to ${end}`;
    return `Mirror ${objects(a.ids)}${line}${a.keepOriginal === true ? ', keeping the originals' : ''}`;
  },
  update_entity_properties: describeProperties,
  move_room: (a) => `Move ${room(a)}${by(a)}`,
  resize_room: (a) => {
    const both = size(a.width, a.length, a);
    if (both.length > 0) return `Resize ${room(a)} to${both}`;
    const single = length('width', a.width, a) || length('length', a.length, a);
    return single.length > 0 ? `Resize ${room(a)} to${single}` : `Resize ${room(a)}`;
  },
  delete_entities: (a) => `Delete ${objects(a.ids)}`,

  add_dimension: (a) => {
    const kind = str(a.kind);
    return `Add ${kind !== undefined ? `${shorten(kind, 12)} ` : ''}dimension${fromTo(a.from, a.to, a)}`;
  },
  add_room_labels: (a) =>
    Array.isArray(a.roomIds) ? `Add labels to ${plural(a.roomIds.length, 'room')}` : 'Add labels to all rooms',

  create_layer: (a) => {
    const name = str(a.name);
    return `Create layer${name !== undefined ? ` ${quote(name)}` : ''}`;
  },
  set_current_layer: (a) => {
    const name = str(a.name);
    return name !== undefined ? `Set current layer to ${quote(name)}` : 'Set current layer';
  },
  set_layer_state: describeLayerState,

  validate_drawing: (a) => {
    const checks = stringList(a.checks);
    return checks !== undefined ? `Validate drawing (${checks})` : 'Validate drawing';
  },

  zoom_extents: () => 'Zoom to extents',
  zoom_to_entities: (a) => `Zoom to ${objects(a.ids)}`,
  highlight_entities: (a) => `Highlight ${objects(a.ids)}`,
  clear_highlight: () => 'Clear highlight',

  undo_last_operation: () => 'Undo last operation',
  save_document: () => 'Save drawing',
};

/**
 * One short human line for approval cards and the activity log, for example
 * `Create rectangle 12 x 14 ft at (0, 0)`, `Delete 3 objects`, `Move 2 objects by (3, 0) ft` or
 * `Create layer "Walls"`. Tolerates invalid or missing arguments (it never throws; it then says
 * less). An unknown tool yields `Run <name>`.
 */
export function describeToolCall(name: string, args: unknown): string {
  if (!isToolName(name)) {
    const label = typeof name === 'string' && name.trim().length > 0 ? shorten(name, 60) : 'unknown tool';
    return `Run ${label}`;
  }
  try {
    return shorten(DESCRIBERS[name](isRecord(args) ? args : {}), MAX_LINE_LENGTH);
  } catch {
    return getTool(name)?.title ?? `Run ${name}`;
  }
}
