// Basic geometry tools (write): lines, polylines, rectangles, circles, arcs, text and the batch tool.
import { z } from 'zod';
import {
  MAX_BATCH_ENTITIES,
  MAX_POLYLINE_POINTS,
  MAX_TEXT_LENGTH,
  colorField,
  createdEntitiesOutput,
  defineTool,
  degrees,
  layerField,
  layerNameSchema,
  point,
  positive,
  unitField,
} from './shared';

const RESULT_NOTE = 'Returns { entities: [{ id, type }] } with the id of the new object.';
const UNIT_NOTE = 'Coordinates and sizes are in "unit" (default: the drawing unit).';

// Field sets shared by the single-entity tools and the items of create_entities. Functions, so
// every schema gets its own instances.

const lineFields = () => ({
  start: point('Start point of the line.'),
  end: point('End point of the line (must differ from the start point).'),
});

const polylineFields = () => ({
  points: z
    .array(point('A vertex of the polyline.'))
    .min(2)
    .max(MAX_POLYLINE_POINTS)
    .describe(
      `The vertices in drawing order, 2 to ${MAX_POLYLINE_POINTS} points. Do not repeat the first point at the end; ` +
        'use "closed" instead.',
    ),
  closed: z
    .boolean()
    .optional()
    .describe('True to join the last vertex back to the first (a closed outline). Default false.'),
});

const rectangleFields = () => ({
  corner: point('Lower-left corner of the rectangle before rotation.'),
  width: positive('Size along X before rotation (> 0).'),
  height: positive('Size along Y before rotation (> 0).'),
  rotation: degrees(
    'Rotation in degrees, counter-clockwise, about "corner". Default 0 (sides parallel to the axes).',
  ).optional(),
});

const circleFields = () => ({
  center: point('Centre of the circle.'),
  radius: positive('Radius (> 0). Note: radius, not diameter.'),
});

const arcFields = () => ({
  center: point('Centre of the circle the arc lies on.'),
  radius: positive('Radius (> 0).'),
  startAngle: degrees('Angle of the arc\'s start point in degrees, counter-clockwise from +X.'),
  endAngle: degrees(
    'Angle of the arc\'s end point in degrees. The arc always runs COUNTER-CLOCKWISE from startAngle to endAngle ' +
      '(for example 0 to 90 is the upper-right quarter; 90 to 0 is the other three quarters).',
  ),
});

const textFields = () => ({
  position: point('Insertion point: the lower-left corner of the text before rotation.'),
  text: z
    .string()
    .min(1)
    .max(MAX_TEXT_LENGTH)
    .describe(`The text to show, 1 to ${MAX_TEXT_LENGTH} characters.`),
  height: positive('Text height (> 0), in the call\'s unit (a length in the drawing, not a font size in points).'),
  rotation: degrees('Rotation in degrees, counter-clockwise, about "position". Default 0 (reads left to right).').optional(),
});

const styleFields = () => ({
  layer: layerField,
  color: colorField,
});

const itemLayerField = layerNameSchema
  .optional()
  .describe(
    'Name of the EXISTING layer for this item. Omit to use the call\'s top-level "layer", or the current layer ' +
      'when that is omitted too.',
  );

const itemStyleFields = () => ({
  layer: itemLayerField,
  color: colorField,
});

/** One item of `create_entities`: the fields of the matching single tool plus the `type` tag. */
export const batchEntitySchema = z.discriminatedUnion('type', [
  z.strictObject({
    type: z.literal('line').describe('"line": a straight segment from "start" to "end".'),
    ...lineFields(),
    ...itemStyleFields(),
  }),
  z.strictObject({
    type: z.literal('polyline').describe('"polyline": connected segments through "points", optionally closed.'),
    ...polylineFields(),
    ...itemStyleFields(),
  }),
  z.strictObject({
    type: z.literal('rectangle').describe('"rectangle": from "corner" with "width" and "height".'),
    ...rectangleFields(),
    ...itemStyleFields(),
  }),
  z.strictObject({
    type: z.literal('circle').describe('"circle": "center" and "radius".'),
    ...circleFields(),
    ...itemStyleFields(),
  }),
  z.strictObject({
    type: z.literal('arc').describe('"arc": counter-clockwise from "startAngle" to "endAngle" around "center".'),
    ...arcFields(),
    ...itemStyleFields(),
  }),
  z.strictObject({
    type: z.literal('text').describe('"text": a single-line text at "position".'),
    ...textFields(),
    ...itemStyleFields(),
  }),
]);
export type BatchEntity = z.infer<typeof batchEntitySchema>;

export const geometryTools = {
  create_line: defineTool('create_line', {
    title: 'Create line',
    description: [
      'Draws one straight line segment from "start" to "end". Use it for single lines; to draw several objects',
      'at once prefer create_entities (one undo step), and for walls use create_wall.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'geometry',
    risk: 'write',
    input: z.strictObject({ ...lineFields(), ...styleFields(), unit: unitField }),
    output: createdEntitiesOutput,
  }),

  create_polyline: defineTool('create_polyline', {
    title: 'Create polyline',
    description: [
      'Draws one polyline: straight segments through the given vertices, in order, as a single object. Set',
      '"closed" to true for a closed outline (for example an L-shaped room outline or a plot of irregular shape).',
      'Use create_rectangle for axis-aligned or rotated rectangles.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'geometry',
    risk: 'write',
    input: z.strictObject({ ...polylineFields(), ...styleFields(), unit: unitField }),
    output: createdEntitiesOutput,
  }),

  create_rectangle: defineTool('create_rectangle', {
    title: 'Create rectangle',
    description: [
      'Draws one rectangle from its lower-left "corner" with "width" along X and "height" along Y, optionally',
      'rotated counter-clockwise about that corner. The rectangle stays one editable object. Use it for simple',
      'outlines, furniture and fixtures; use create_room for a room with walls.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'geometry',
    risk: 'write',
    input: z.strictObject({ ...rectangleFields(), ...styleFields(), unit: unitField }),
    output: createdEntitiesOutput,
  }),

  create_circle: defineTool('create_circle', {
    title: 'Create circle',
    description: [
      'Draws one full circle from its "center" and "radius" (half the diameter).',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'geometry',
    risk: 'write',
    input: z.strictObject({ ...circleFields(), ...styleFields(), unit: unitField }),
    output: createdEntitiesOutput,
  }),

  create_arc: defineTool('create_arc', {
    title: 'Create arc',
    description: [
      'Draws one circular arc around "center" with "radius", running COUNTER-CLOCKWISE from "startAngle" to',
      '"endAngle". Angles are degrees measured counter-clockwise from the +X axis (0 = right, 90 = up). Use it',
      'for door swings, rounded corners and curved edges.',
      'The centre and radius are in "unit" (default: the drawing unit).',
      RESULT_NOTE,
    ],
    category: 'geometry',
    risk: 'write',
    input: z.strictObject({ ...arcFields(), ...styleFields(), unit: unitField }),
    output: createdEntitiesOutput,
  }),

  create_text: defineTool('create_text', {
    title: 'Create text',
    description: [
      'Places one single-line text (a note, a label, a title) with its lower-left corner at "position". "height"',
      'is the text height as a drawing length, in the call\'s unit. For room name/area labels prefer',
      'add_room_labels.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'geometry',
    risk: 'write',
    input: z.strictObject({ ...textFields(), ...styleFields(), unit: unitField }),
    output: createdEntitiesOutput,
  }),

  create_entities: defineTool('create_entities', {
    title: 'Create entities',
    description: [
      `Creates many basic entities in ONE call and ONE undo step: 1 to ${MAX_BATCH_ENTITIES} items, each tagged`,
      'with "type" ("line", "polyline", "rectangle", "circle", "arc" or "text") and carrying the same fields as the',
      'matching single tool (create_line, create_polyline, ...), without "unit". Prefer this over repeated single',
      'calls whenever you draw more than one object: it is faster and the user can undo it at once. Either every',
      'item is created or, if any item is invalid, nothing is. Each item may set its own "layer" and "color"; the',
      'top-level "layer" is the default for items without one. All coordinates and sizes of all items are in the',
      'top-level "unit" (default: the drawing unit). Returns { entities: [{ id, type }] } in the order of the items.',
    ],
    category: 'geometry',
    risk: 'write',
    input: z.strictObject({
      entities: z
        .array(batchEntitySchema)
        .min(1)
        .max(MAX_BATCH_ENTITIES)
        .describe(`The entities to create, 1 to ${MAX_BATCH_ENTITIES}, in creation order.`),
      layer: layerNameSchema
        .optional()
        .describe(
          'Name of the EXISTING layer for items that do not set their own "layer". Omit to use the current layer.',
        ),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),
} as const;
