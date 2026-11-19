// Building blocks shared by every tool definition: field schemas with model-facing descriptions,
// common result shapes and the typed `defineTool` helper. Browser-safe.
import { z } from 'zod';
import type { ToolCategory, ToolDefinition, ToolRisk } from '../core';
import type { ToolName } from './names';

// ---------------------------------------------------------------------------------------------
// Limits
// ---------------------------------------------------------------------------------------------

/** Longest accepted entity / room id. */
export const MAX_ID_LENGTH = 128;
/** Most ids accepted in one id list. */
export const MAX_ID_COUNT = 500;
/** Longest accepted layer name. */
export const MAX_LAYER_NAME_LENGTH = 255;
/** Most entities one `create_entities` call may create. */
export const MAX_BATCH_ENTITIES = 500;
/** Most vertices of one polyline. */
export const MAX_POLYLINE_POINTS = 2000;
/** Longest text of a text entity. */
export const MAX_TEXT_LENGTH = 2000;
/** Largest page size of the inspection tools. */
export const MAX_INSPECT_LIMIT = 200;
/** Page size of the inspection tools when `limit` is omitted. */
export const DEFAULT_INSPECT_LIMIT = 50;

// ---------------------------------------------------------------------------------------------
// Typed tool definition
// ---------------------------------------------------------------------------------------------

/** A catalog entry that keeps its literal name and the exact zod schema types. */
export interface CatalogTool<
  N extends ToolName = ToolName,
  I extends z.ZodType = z.ZodType,
  O extends z.ZodType = z.ZodType,
> extends ToolDefinition {
  readonly name: N;
  readonly input: I;
  readonly output: O;
}

/** Declares one catalog tool; the description may be given as lines that are joined with spaces. */
export function defineTool<const N extends ToolName, I extends z.ZodType, O extends z.ZodType>(
  name: N,
  spec: {
    title: string;
    description: string | readonly string[];
    category: ToolCategory;
    risk: ToolRisk;
    input: I;
    output: O;
  },
): CatalogTool<N, I, O> {
  const description = typeof spec.description === 'string' ? spec.description : spec.description.join(' ');
  return Object.freeze({
    name,
    title: spec.title,
    description,
    category: spec.category,
    risk: spec.risk,
    input: spec.input,
    output: spec.output,
  });
}

// ---------------------------------------------------------------------------------------------
// Input fields
// ---------------------------------------------------------------------------------------------

/** The unit values, as a tuple for `z.enum`. */
export const UNIT_VALUES = ['mm', 'cm', 'm', 'in', 'ft'] as const;

/** Schema of a length unit. */
export const unitSchema = z.enum(UNIT_VALUES);

/** Optional `unit` argument of every length- or coordinate-bearing tool. */
export const unitField = unitSchema
  .optional()
  .describe(
    'Unit of ALL lengths and coordinates in this call and in its result: "mm", "cm", "m", "in" or "ft". ' +
      "Omit to use the drawing's own unit (reported by get_drawing_info).",
  );

/** A strict `{ x, y }` point argument with the given description. */
export function point(description: string) {
  return z
    .strictObject({
      x: z.number().describe('X coordinate (positive to the right), in the call\'s unit.'),
      y: z.number().describe('Y coordinate (positive up the page), in the call\'s unit.'),
    })
    .describe(description);
}

/** A strictly positive length / size argument. */
export function positive(description: string) {
  return z.number().positive().describe(description);
}

/** A length that may be zero (for example a setback or a sill height). */
export function nonNegative(description: string) {
  return z.number().nonnegative().describe(description);
}

/** Any finite number (signed distance, coordinate delta). */
export function finite(description: string) {
  return z.number().describe(description);
}

/** An angle in degrees, counter-clockwise from +X. */
export function degrees(description: string) {
  return z.number().describe(description);
}

/** Schema of one opaque id. */
export const idSchema = z.string().min(1).max(MAX_ID_LENGTH);

/** A single id argument. */
export function id(description: string) {
  return idSchema.describe(description);
}

/** A list of 1..500 ids. */
export function idList(description: string) {
  return z.array(idSchema).min(1).max(MAX_ID_COUNT).describe(description);
}

/** Schema of a layer name. */
export const layerNameSchema = z.string().min(1).max(MAX_LAYER_NAME_LENGTH);

/** Optional target layer of a new object. */
export const layerField = layerNameSchema
  .optional()
  .describe(
    'Name of the EXISTING layer to put the new object on (see list_layers; create a missing layer first with ' +
      'create_layer). Omit to use the current layer.',
  );

const COLOR_PATTERN = /^(#[0-9a-fA-F]{6}|bylayer)$/;
const HEX_COLOR_PATTERN = /^#[0-9a-fA-F]{6}$/;

/** '#rrggbb' or 'bylayer'. */
export const colorSchema = z
  .string()
  .regex(COLOR_PATTERN, 'must be a "#rrggbb" hex colour (for example "#ff0000") or "bylayer"');

/** '#rrggbb' only (layers have no "bylayer"). */
export const hexColorSchema = z
  .string()
  .regex(HEX_COLOR_PATTERN, 'must be a "#rrggbb" hex colour (for example "#ff0000")');

/** Optional colour of a new object. */
export const colorField = colorSchema
  .optional()
  .describe(
    'Colour as "#rrggbb" (for example "#ff0000"), or "bylayer" to follow the layer\'s colour. Omit for "bylayer".',
  );

/** Optional page size of the inspection tools (default 50, at most 200). */
export const limitField = z
  .number()
  .int()
  .min(1)
  .max(MAX_INSPECT_LIMIT)
  .default(DEFAULT_INSPECT_LIMIT)
  .describe(
    `Maximum number of objects to return, 1 to ${MAX_INSPECT_LIMIT} (default ${DEFAULT_INSPECT_LIMIT}). ` +
      'The result reports "total" and "truncated" so you can tell when there are more.',
  );

/** Optional list of entity type names to filter by. */
export function typesFilter(description: string) {
  return z.array(z.string().min(1).max(64)).min(1).max(50).optional().describe(description);
}

/** Optional list of layer names to filter by. */
export function layersFilter(description: string) {
  return z.array(layerNameSchema).min(1).max(200).optional().describe(description);
}

/** Room types understood by the architectural tools. */
export const ROOM_TYPES = [
  'bedroom',
  'master_bedroom',
  'living_room',
  'dining_room',
  'kitchen',
  'bathroom',
  'toilet',
  'balcony',
  'utility',
  'store',
  'study',
  'corridor',
  'garage',
  'lobby',
  'other',
] as const;
export type RoomType = (typeof ROOM_TYPES)[number];

/** Schema of a room type. */
export const roomTypeSchema = z.enum(ROOM_TYPES);

/** Entity kinds the basic geometry tools create. */
export const GEOMETRY_ENTITY_TYPES = ['line', 'polyline', 'rectangle', 'circle', 'arc', 'text'] as const;
export type GeometryEntityType = (typeof GEOMETRY_ENTITY_TYPES)[number];

/** True when at least one of the two room selectors (`roomId`, `name`) is present. */
export function hasRoomSelector(value: { roomId?: string | undefined; name?: string | undefined }): boolean {
  return value.roomId !== undefined || value.name !== undefined;
}

/** Message of the room-selector refinement. */
export const ROOM_SELECTOR_MESSAGE = 'Provide "roomId" or "name" to say which room is meant';

/** Arguments of a tool that takes none. */
export const noArguments = z.strictObject({});

// ---------------------------------------------------------------------------------------------
// Result (`ToolResult.data`) shapes. Loose objects: a CAD may add its own fields.
// ---------------------------------------------------------------------------------------------

/** A point in a result. */
export const pointOutput = z.object({ x: z.number(), y: z.number() });

/** An axis-aligned bounding box in a result. */
export const boxOutput = z.object({
  min: pointOutput.describe('Lower-left corner.'),
  max: pointOutput.describe('Upper-right corner.'),
});

/** `{ id, type }` of one entity. */
export const entityRefSchema = z.looseObject({
  id: z.string().describe('Opaque entity id; pass it to the inspection and modification tools.'),
  type: z.string().describe('Entity type, for example "line", "rectangle", "wall" or "door".'),
});
export type EntityRef = z.infer<typeof entityRefSchema>;

/** Result of every tool that creates entities: the created objects, in creation order. */
export const createdEntitiesOutput = z.looseObject({
  entities: z.array(entityRefSchema).describe('The created entities, in creation order.'),
});

/** Result of the tools that change existing entities. */
export const modifiedEntitiesOutput = z.looseObject({
  entities: z
    .array(entityRefSchema)
    .optional()
    .describe('The entities that were changed (their ids are also in ToolResult.modifiedIds).'),
});

/** Result of a tool that has nothing to report beyond the ToolResult envelope. */
export const emptyOutput = z.looseObject({});

/**
 * Compact description of one entity as returned by the inspection tools. `geometry` depends on
 * `type`; see {@link EntityGeometryByType} for the basic entity kinds.
 */
export const entitySummarySchema = z.looseObject({
  id: z.string().describe('Opaque entity id.'),
  type: z.string().describe('Entity type: "line", "polyline", "rectangle", "circle", "arc", "text", "wall", ...'),
  layer: z.string().describe('Name of the layer the entity is on.'),
  color: z.string().describe('"#rrggbb" or "bylayer".'),
  bbox: boxOutput.describe('Axis-aligned bounding box, in the unit of the call.'),
  length: z.number().optional().describe('Length / perimeter in the unit of the call, where meaningful.'),
  area: z.number().optional().describe('Enclosed area in square units of the call, for closed shapes.'),
  geometry: z
    .record(z.string(), z.unknown())
    .describe(
      'Defining geometry by type. line: start, end. polyline: points, closed. rectangle: corner, width, ' +
        'height, rotation. circle: center, radius. arc: center, radius, startAngle, endAngle. text: position, ' +
        'text, height, rotation. Architectural objects: their defining fields. Angles in degrees.',
    ),
});
export type EntitySummary = z.infer<typeof entitySummarySchema>;

/** Result of inspect_entities, get_selected_objects and find_objects. */
export const entityListOutput = z.looseObject({
  total: z.number().int().nonnegative().describe('Number of matching entities before limit/offset.'),
  entities: z.array(entitySummarySchema).describe('The returned page of entities.'),
  truncated: z.boolean().describe('True when more matching entities exist than were returned.'),
});

/** `geometry` of an EntitySummary for the basic entity kinds (lengths in the call\'s unit, angles in degrees). */
export interface EntityGeometryByType {
  line: { start: { x: number; y: number }; end: { x: number; y: number } };
  polyline: { points: { x: number; y: number }[]; closed: boolean };
  rectangle: { corner: { x: number; y: number }; width: number; height: number; rotation: number };
  circle: { center: { x: number; y: number }; radius: number };
  arc: { center: { x: number; y: number }; radius: number; startAngle: number; endAngle: number };
  text: { position: { x: number; y: number }; text: string; height: number; rotation: number };
}

/** Details of one room, as returned by inspect_room and the room tools. */
export const roomDetailsSchema = z.looseObject({
  id: z.string().optional().describe('Room id, usable as "roomId".'),
  name: z.string().describe('Room name, for example "Master Bedroom".'),
  roomType: z.string().describe('Room type, for example "bedroom" or "kitchen".'),
  width: z.number().describe('Interior size along X.'),
  length: z.number().describe('Interior size along Y.'),
  area: z.number().describe('Interior floor area in square units of the call.'),
  origin: pointOutput.describe('Lower-left interior corner.'),
  wallIds: z.array(z.string()).describe('Ids of the walls enclosing the room.'),
  doorIds: z.array(z.string()).describe('Ids of the doors in those walls.'),
  windowIds: z.array(z.string()).describe('Ids of the windows in those walls.'),
});
export type RoomDetails = z.infer<typeof roomDetailsSchema>;

/** One layer, as returned by list_layers. */
export const layerSummarySchema = z.looseObject({
  name: z.string().describe('Layer name.'),
  color: z.string().describe('Layer colour as "#rrggbb".'),
  visible: z.boolean().describe('False when the layer is hidden.'),
  locked: z.boolean().describe('True when objects on the layer cannot be edited.'),
  current: z.boolean().describe('True for the layer new objects go on by default.'),
  entityCount: z.number().int().nonnegative().describe('Number of entities on the layer.'),
});
export type LayerSummary = z.infer<typeof layerSummarySchema>;
