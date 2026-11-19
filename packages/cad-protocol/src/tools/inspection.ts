// Inspection tools (read-only): what is in the drawing, on which layers, what is selected.
import { z } from 'zod';
import {
  ROOM_SELECTOR_MESSAGE,
  boxOutput,
  defineTool,
  entityListOutput,
  hasRoomSelector,
  id,
  idList,
  layerSummarySchema,
  layersFilter,
  limitField,
  noArguments,
  point,
  positive,
  roomDetailsSchema,
  typesFilter,
  unitField,
  unitSchema,
} from './shared';

export const inspectionTools = {
  get_drawing_info: defineTool('get_drawing_info', {
    title: 'Get drawing info',
    description: [
      'Returns a summary of the active drawing: its name and file path, its length unit, how many entities it',
      'contains (total and per type), the number of layers, the current layer, the extents (bounding box of',
      'everything drawn, null when the drawing is empty), how many objects are selected and the drawing revision.',
      'Call this first in a session and whenever you need the drawing unit or overall size. Extents are in the',
      "drawing's unit. Takes no arguments.",
    ],
    category: 'inspection',
    risk: 'read',
    input: noArguments,
    output: z.looseObject({
      name: z.string().describe('Drawing name, for example "Plan.cadstudio" or "Untitled".'),
      path: z.string().nullable().describe('Full file path, or null when the drawing was never saved.'),
      unit: unitSchema.describe("The drawing's length unit; the default unit of every tool call."),
      entityCount: z.number().int().nonnegative().describe('Total number of entities.'),
      entityCountsByType: z
        .record(z.string(), z.number().int().nonnegative())
        .describe('Number of entities per entity type.'),
      layerCount: z.number().int().nonnegative().describe('Number of layers.'),
      currentLayer: z.string().describe('Name of the current layer.'),
      extents: boxOutput.nullable().describe("Bounding box of all entities in the drawing's unit, or null when empty."),
      selectionCount: z.number().int().nonnegative().describe('Number of selected entities.'),
      revision: z.number().int().nonnegative().describe('Drawing revision (increases with every change).'),
    }),
  }),

  list_layers: defineTool('list_layers', {
    title: 'List layers',
    description: [
      'Lists every layer of the drawing with its colour, whether it is visible, locked or the current layer, and',
      'how many entities are on it. Use it before putting objects on a layer (a layer must exist before it can',
      'be used) and before changing layer states. Takes no arguments.',
    ],
    category: 'inspection',
    risk: 'read',
    input: noArguments,
    output: z.looseObject({
      layers: z
        .array(layerSummarySchema)
        .describe('All layers, in the drawing\'s layer order.'),
    }),
  }),

  inspect_entities: defineTool('inspect_entities', {
    title: 'Inspect entities',
    description: [
      'Returns the geometry and properties of entities: id, type, layer, colour, bounding box, length/area where',
      'meaningful and the defining geometry (line: start/end; polyline: points, closed; rectangle: corner, width,',
      'height, rotation; circle: center, radius; arc: center, radius, startAngle, endAngle; text: position, text,',
      'height, rotation). Filter by ids, entity types, layer names and/or a window ("within"); filters combine with',
      'AND, and with no filter every entity matches. Results are paged: "total" is the number of matches,',
      '"truncated" tells you more exist; continue with "offset". Use this to read exact coordinates before',
      'modifying or dimensioning objects. Lengths and coordinates come back in "unit" (default: the drawing unit);',
      'angles are degrees counter-clockwise from +X.',
    ],
    category: 'inspection',
    risk: 'read',
    input: z.strictObject({
      ids: idList('Only these entity ids (1 to 500). Ids that do not exist are ignored.').optional(),
      types: typesFilter(
        'Only entities of these types, for example ["line", "rectangle"] or ["wall", "door"]. Case-insensitive.',
      ),
      layers: layersFilter('Only entities on these layers (layer names).'),
      within: z
        .strictObject({
          min: point('Lower-left corner of the window.'),
          max: point('Upper-right corner of the window.'),
        })
        .optional()
        .describe('Only entities whose bounding box touches this axis-aligned window.'),
      limit: limitField,
      offset: z
        .number()
        .int()
        .min(0)
        .default(0)
        .describe('Number of matching entities to skip (for paging; default 0).'),
      unit: unitField,
    }),
    output: entityListOutput,
  }),

  get_selected_objects: defineTool('get_selected_objects', {
    title: 'Get selected objects',
    description: [
      'Returns the entities the user currently has selected in the CAD window, in the same shape as',
      'inspect_entities ({ total, entities, truncated }). Use it when the user refers to "this", "these" or "the',
      'selected" objects. An empty list means nothing is selected. Lengths and coordinates come back in "unit"',
      '(default: the drawing unit).',
    ],
    category: 'inspection',
    risk: 'read',
    input: z.strictObject({
      limit: limitField,
      unit: unitField,
    }),
    output: entityListOutput,
  }),

  find_objects: defineTool('find_objects', {
    title: 'Find objects',
    description: [
      'Searches the drawing for entities and returns them in the same shape as inspect_entities ({ total,',
      'entities, truncated }). "query" is a case-insensitive text match on the entity type, layer name, room or',
      'wall name and text content (for example "kitchen", "door", "dimension"). Narrow further by entity types,',
      'layer names and/or proximity to a point ("near"). All given criteria must match. Use it to locate objects',
      'the user names or describes before inspecting or modifying them. Lengths and coordinates are in "unit"',
      '(default: the drawing unit).',
    ],
    category: 'inspection',
    risk: 'read',
    input: z.strictObject({
      query: z
        .string()
        .min(1)
        .max(200)
        .optional()
        .describe(
          'Case-insensitive text to look for in the entity type, layer name, room/wall name and text content.',
        ),
      types: typesFilter('Only entities of these types, for example ["text"] or ["wall"]. Case-insensitive.'),
      layers: layersFilter('Only entities on these layers (layer names).'),
      near: z
        .strictObject({
          point: point('Centre of the search circle.'),
          radius: positive('Search radius (> 0), in the call\'s unit.'),
        })
        .optional()
        .describe('Only entities that come within "radius" of "point".'),
      limit: limitField,
      unit: unitField,
    }),
    output: entityListOutput,
  }),

  inspect_room: defineTool('inspect_room', {
    title: 'Inspect room',
    description: [
      'Returns the details of one room: name, room type, interior width (along X) and length (along Y), floor',
      'area, origin (lower-left interior corner) and the ids of its walls, doors and windows. Identify the room by',
      '"roomId" or by "name" (case-insensitive); at least one of them is required and "roomId" wins when both are',
      'given. Use it before moving, resizing or furnishing a room. Sizes are in "unit" (default: the drawing unit),',
      'area in that unit squared.',
    ],
    category: 'inspection',
    risk: 'read',
    input: z
      .strictObject({
        roomId: id('Id of the room (from create_room, create_floor_plan or find_objects).').optional(),
        name: z
          .string()
          .min(1)
          .max(200)
          .optional()
          .describe('Name of the room, for example "Kitchen" (case-insensitive). Used when "roomId" is omitted.'),
        unit: unitField,
      })
      .refine(hasRoomSelector, { message: ROOM_SELECTOR_MESSAGE }),
    output: roomDetailsSchema,
  }),

  get_drawing_revision: defineTool('get_drawing_revision', {
    title: 'Get drawing revision',
    description: [
      'Returns the current drawing revision, a counter that increases with every change (by you or by the user).',
      'Use it as a cheap check for whether the drawing changed since you last inspected it. Takes no arguments.',
    ],
    category: 'inspection',
    risk: 'read',
    input: noArguments,
    output: z.looseObject({
      revision: z.number().int().nonnegative().describe('Current drawing revision.'),
    }),
  }),
} as const;
