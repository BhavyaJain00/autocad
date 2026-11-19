// Modification tools: transform, restyle and delete entities; move and resize rooms.
import { z } from 'zod';
import {
  ROOM_SELECTOR_MESSAGE,
  colorSchema,
  createdEntitiesOutput,
  defineTool,
  degrees,
  entityRefSchema,
  finite,
  hasRoomSelector,
  id,
  idList,
  layerNameSchema,
  modifiedEntitiesOutput,
  point,
  positive,
  roomDetailsSchema,
  unitField,
} from './shared';

const IDS_NOTE = 'Get ids from inspect_entities, find_objects, get_selected_objects or the result of a create tool.';

const roomSelectorFields = () => ({
  roomId: id('Id of the room. Required unless "name" is given; wins when both are given.').optional(),
  name: z
    .string()
    .min(1)
    .max(200)
    .optional()
    .describe('Name of the room (case-insensitive), for example "Kitchen". Required unless "roomId" is given.'),
});

const roomOutput = z.looseObject({
  room: roomDetailsSchema.optional().describe('The room after the change.'),
  entities: z.array(entityRefSchema).optional().describe('Every object that was changed.'),
});

export const modificationTools = {
  move_entities: defineTool('move_entities', {
    title: 'Move entities',
    description: [
      'Moves the given entities by the displacement ("dx", "dy") without changing their shape. Positive dx moves',
      'right, positive dy moves up.',
      IDS_NOTE,
      'The displacement is in "unit" (default: the drawing unit). All entities move in one undo step; if the call fails',
      'nothing is moved. Returns the ids of the moved entities in modifiedIds.',
    ],
    category: 'modification',
    risk: 'write',
    input: z.strictObject({
      ids: idList('Ids of the entities to move (1 to 500).'),
      dx: finite('Displacement along X (positive = right).'),
      dy: finite('Displacement along Y (positive = up).'),
      unit: unitField,
    }),
    output: modifiedEntitiesOutput,
  }),

  copy_entities: defineTool('copy_entities', {
    title: 'Copy entities',
    description: [
      'Copies the given entities, leaving the originals in place. With "count" > 1 it makes an array: copy k',
      '(k = 1..count) is offset by k times ("dx", "dy") from the original, for example count 3 with dx 1000 puts',
      'copies at +1000, +2000 and +3000.',
      IDS_NOTE,
      'The displacement is in "unit" (default: the drawing unit). Returns { entities: [{ id, type }] } with the new',
      'copies, also listed in createdIds.',
    ],
    category: 'modification',
    risk: 'write',
    input: z.strictObject({
      ids: idList('Ids of the entities to copy (1 to 500).'),
      dx: finite('Offset of each successive copy along X.'),
      dy: finite('Offset of each successive copy along Y.'),
      count: z
        .number()
        .int()
        .min(1)
        .max(100)
        .default(1)
        .describe('Number of copies of each entity, 1 to 100 (default 1). Copy k is offset by k times (dx, dy).'),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),

  rotate_entities: defineTool('rotate_entities', {
    title: 'Rotate entities',
    description: [
      'Rotates the given entities about "center" by "angle" degrees; positive angles turn counter-clockwise,',
      'negative clockwise.',
      IDS_NOTE,
      '"center" is in "unit" (default: the drawing unit). One undo step; returns the rotated ids in modifiedIds.',
    ],
    category: 'modification',
    risk: 'write',
    input: z.strictObject({
      ids: idList('Ids of the entities to rotate (1 to 500).'),
      center: point('Point the entities turn about.'),
      angle: degrees('Rotation in degrees; positive = counter-clockwise, negative = clockwise.'),
      unit: unitField,
    }),
    output: modifiedEntitiesOutput,
  }),

  scale_entities: defineTool('scale_entities', {
    title: 'Scale entities',
    description: [
      'Scales the given entities uniformly about "center" by "factor": 2 doubles every size, 0.5 halves it. The',
      'point "center" stays where it is.',
      IDS_NOTE,
      '"center" is in "unit" (default: the drawing unit). One undo step; returns the scaled ids in modifiedIds.',
    ],
    category: 'modification',
    risk: 'write',
    input: z.strictObject({
      ids: idList('Ids of the entities to scale (1 to 500).'),
      center: point('Fixed point of the scaling.'),
      factor: positive('Scale factor (> 0): above 1 enlarges, below 1 shrinks. A plain ratio, not a length.'),
      unit: unitField,
    }),
    output: modifiedEntitiesOutput,
  }),

  mirror_entities: defineTool('mirror_entities', {
    title: 'Mirror entities',
    description: [
      'Mirrors the given entities across the infinite line through "lineStart" and "lineEnd" (two different',
      'points). By default the originals are replaced by their mirror images; set "keepOriginal" to true to keep',
      'them and add mirrored copies (symmetric layouts).',
      IDS_NOTE,
      'The line points are in "unit" (default: the drawing unit). One undo step. Mirrored-in-place ids come back',
      'in modifiedIds; new copies in createdIds and in { entities: [{ id, type }] }.',
    ],
    category: 'modification',
    risk: 'write',
    input: z.strictObject({
      ids: idList('Ids of the entities to mirror (1 to 500).'),
      lineStart: point('First point of the mirror line.'),
      lineEnd: point('Second point of the mirror line (must differ from the first).'),
      keepOriginal: z
        .boolean()
        .optional()
        .describe('True to keep the originals and create mirrored copies. Default false (mirror in place).'),
      unit: unitField,
    }),
    output: modifiedEntitiesOutput,
  }),

  update_entity_properties: defineTool('update_entity_properties', {
    title: 'Update entity properties',
    description: [
      'Changes the layer and/or the colour of the given entities without touching their geometry. At least one of',
      '"layer" and "color" is REQUIRED. The layer must already exist (create it with create_layer).',
      IDS_NOTE,
      'One undo step; returns the changed ids in modifiedIds.',
    ],
    category: 'modification',
    risk: 'write',
    input: z
      .strictObject({
        ids: idList('Ids of the entities to change (1 to 500).'),
        layer: layerNameSchema.optional().describe('Name of the EXISTING layer to move the entities to.'),
        color: colorSchema
          .optional()
          .describe('New colour: "#rrggbb" (for example "#00aa00"), or "bylayer" to follow the layer\'s colour.'),
      })
      .refine((value) => value.layer !== undefined || value.color !== undefined, {
        message: 'Provide at least one of "layer" and "color"',
      }),
    output: modifiedEntitiesOutput,
  }),

  move_room: defineTool('move_room', {
    title: 'Move room',
    description: [
      'Moves a whole room (its outline, walls, doors, windows and label together) by ("dx", "dy"). Identify the',
      'room by "roomId" or "name"; at least one is REQUIRED. Use this instead of move_entities for rooms so walls',
      'and openings stay attached.',
      'The displacement is in "unit" (default: the drawing unit). One undo step; returns { room } with the new',
      'position.',
    ],
    category: 'modification',
    risk: 'write',
    input: z
      .strictObject({
        ...roomSelectorFields(),
        dx: finite('Displacement along X (positive = right).'),
        dy: finite('Displacement along Y (positive = up).'),
        unit: unitField,
      })
      .refine(hasRoomSelector, { message: ROOM_SELECTOR_MESSAGE }),
    output: roomOutput,
  }),

  resize_room: defineTool('resize_room', {
    title: 'Resize room',
    description: [
      'Changes the interior size of a room and rebuilds its walls, keeping doors, windows and the label attached.',
      'Identify the room by "roomId" or "name" (at least one is REQUIRED) and give the new "width" (along X)',
      'and/or "length" (along Y); at least one of them is REQUIRED and an omitted one stays as it is. "anchor"',
      'says which point of the room stays fixed: a corner ("sw" = lower-left, the default, "se", "nw", "ne") or',
      'the "center".',
      'Sizes are in "unit" (default: the drawing unit). One undo step; returns { room } with the new size.',
    ],
    category: 'modification',
    risk: 'write',
    input: z
      .strictObject({
        ...roomSelectorFields(),
        width: positive('New interior size along X (> 0). Omit to keep the current width.').optional(),
        length: positive('New interior size along Y (> 0). Omit to keep the current length.').optional(),
        anchor: z
          .enum(['sw', 'se', 'nw', 'ne', 'center'])
          .optional()
          .describe(
            'Point of the room that stays fixed: "sw" (lower-left, default), "se" (lower-right), "nw" (upper-left), ' +
              '"ne" (upper-right) or "center".',
          ),
        unit: unitField,
      })
      .refine(hasRoomSelector, { message: ROOM_SELECTOR_MESSAGE })
      .refine((value) => value.width !== undefined || value.length !== undefined, {
        message: 'Provide at least one of "width" and "length"',
      }),
    output: roomOutput,
  }),

  delete_entities: defineTool('delete_entities', {
    title: 'Delete entities',
    description: [
      'Permanently removes the given entities from the drawing (1 to 500 ids). DESTRUCTIVE: the user is always',
      'asked to approve it. Use it only when the user asked to remove or replace something, and delete only the',
      'ids you have inspected.',
      IDS_NOTE,
      'One undo step (the user can undo it); if the call fails nothing is deleted. Returns the removed ids in',
      'deletedIds.',
    ],
    category: 'modification',
    risk: 'destructive',
    input: z.strictObject({
      ids: idList('Ids of the entities to delete (1 to 500).'),
    }),
    output: z.looseObject({
      deletedCount: z.number().int().nonnegative().optional().describe('Number of entities removed.'),
    }),
  }),
} as const;
