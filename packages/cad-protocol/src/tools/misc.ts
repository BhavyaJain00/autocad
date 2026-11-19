// Annotation, layer, validation, view and document tools.
import { z } from 'zod';
import {
  createdEntitiesOutput,
  defineTool,
  emptyOutput,
  finite,
  hexColorSchema,
  idList,
  layerNameSchema,
  layerSummarySchema,
  noArguments,
  point,
  positive,
  unitField,
} from './shared';

const layerOutput = z.looseObject({
  layer: layerSummarySchema.optional().describe('The layer after the change.'),
});

const viewOutput = emptyOutput;

export const annotationTools = {
  add_dimension: defineTool('add_dimension', {
    title: 'Add dimension',
    description: [
      'Adds one dimension between the points "from" and "to", with the measured value as text. "linear" measures',
      'the horizontal or vertical distance (whichever is larger between the two points); "aligned" measures the',
      'true distance along the line from "from" to "to". "offset" is the signed distance of the dimension line',
      'from the measured points: positive puts it to the left of the direction from "from" to "to" (above a',
      'left-to-right measurement), negative on the other side. Read exact points with inspect_entities first.',
      'Points and offset are in "unit" (default: the drawing unit).',
      'Returns { entities: [{ id, type }] } with the new dimension.',
    ],
    category: 'annotation',
    risk: 'write',
    input: z.strictObject({
      kind: z
        .enum(['linear', 'aligned'])
        .describe('"linear": horizontal or vertical distance. "aligned": true distance, parallel to from-to.'),
      from: point('First measured point.'),
      to: point('Second measured point (must differ from the first).'),
      offset: finite(
        'Signed distance from the measured points to the dimension line; positive = left of the from-to direction. ' +
          'Use a value that clears the geometry, for example 300 to 600 mm.',
      ),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),

  add_room_labels: defineTool('add_room_labels', {
    title: 'Add room labels',
    description: [
      'Adds or refreshes the text label in the middle of rooms: the room name and, unless "showArea" is false, its',
      'floor area. With no "roomIds" every room in the drawing is labelled. Existing labels of those rooms are',
      'updated rather than duplicated. "textHeight" is in "unit" (default: the drawing unit).',
      'Returns { entities: [{ id, type }] } with the label texts.',
    ],
    category: 'annotation',
    risk: 'write',
    input: z.strictObject({
      roomIds: idList('Ids of the rooms to label (1 to 500). Omit to label every room.').optional(),
      showArea: z.boolean().optional().describe('False to show only the room name. Default true (name and area).'),
      textHeight: positive('Text height of the labels (> 0), as a drawing length. Omit for the CAD\'s default.').optional(),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),
} as const;

export const layerTools = {
  create_layer: defineTool('create_layer', {
    title: 'Create layer',
    description: [
      'Creates a new layer with the given name and colour. Layer names are unique (case-insensitive); creating a',
      'layer that already exists fails, so check list_layers first. Create layers before drawing objects on',
      'them, for example "Walls", "Doors", "Furniture", "Dimensions". Set "setCurrent" to true to make new objects',
      'go on this layer by default. Returns { layer } describing the new layer.',
    ],
    category: 'layers',
    risk: 'write',
    input: z.strictObject({
      name: layerNameSchema.describe('Name of the new layer, 1 to 255 characters, for example "Walls".'),
      color: hexColorSchema
        .optional()
        .describe('Layer colour as "#rrggbb" (for example "#ffcc00"). Omit for the CAD\'s default colour.'),
      setCurrent: z.boolean().optional().describe('True to make this the current layer. Default false.'),
    }),
    output: layerOutput,
  }),

  set_current_layer: defineTool('set_current_layer', {
    title: 'Set current layer',
    description: [
      'Makes an existing layer the current layer: objects created without an explicit "layer" go on it. The layer',
      'must exist (see list_layers). Returns { layer } describing the now-current layer.',
    ],
    category: 'layers',
    risk: 'write',
    input: z.strictObject({
      name: layerNameSchema.describe('Name of the existing layer to make current.'),
    }),
    output: layerOutput,
  }),

  set_layer_state: defineTool('set_layer_state', {
    title: 'Set layer state',
    description: [
      'Changes an existing layer: show or hide it ("visible"), lock or unlock it ("locked"), recolour it ("color")',
      'and/or rename it ("newName"). At least one of these changes is REQUIRED. Objects on a locked layer cannot be',
      'modified or deleted; objects on a hidden layer are not drawn. Returns { layer } with the new state.',
    ],
    category: 'layers',
    risk: 'write',
    input: z
      .strictObject({
        name: layerNameSchema.describe('Name of the existing layer to change.'),
        visible: z.boolean().optional().describe('True to show the layer, false to hide it.'),
        locked: z.boolean().optional().describe('True to lock the layer against edits, false to unlock it.'),
        color: hexColorSchema.optional().describe('New layer colour as "#rrggbb".'),
        newName: layerNameSchema.optional().describe('New name for the layer, 1 to 255 characters (must not be taken).'),
      })
      .refine(
        (value) =>
          value.visible !== undefined ||
          value.locked !== undefined ||
          value.color !== undefined ||
          value.newName !== undefined,
        { message: 'Provide at least one change: "visible", "locked", "color" or "newName"' },
      ),
    output: layerOutput,
  }),
} as const;

export const validationTools = {
  validate_drawing: defineTool('validate_drawing', {
    title: 'Validate drawing',
    description: [
      'Checks the drawing for common drafting problems and returns them without changing anything, for example',
      'overlapping or duplicate objects, self-intersecting outlines, tiny gaps between endpoints, objects on hidden',
      'or locked layers, room boundaries that are not closed or doors/windows outside a wall. Which checks exist',
      'depends on the CAD application. Run it after finishing a set of changes and report what it finds. It does',
      'NOT check building codes or regulations.',
      'Returns { issues: [{ severity: "error" | "warning" | "info", code, message, entityIds }], checked: string[] }',
      'where "checked" names the checks that ran.',
    ],
    category: 'validation',
    risk: 'read',
    input: z.strictObject({
      checks: z
        .array(z.string().min(1).max(64))
        .min(1)
        .max(50)
        .optional()
        .describe(
          'Names of the checks to run. They depend on the CAD application (CAD Studio: "duplicates", ' +
            '"self_intersections", "tiny_gaps", "hidden_or_locked"); an unknown name is reported as a warning and ' +
            'skipped. Usually omit it to run every check the CAD supports; the result\'s "checked" lists the ones that ran.',
        ),
    }),
    output: z.looseObject({
      issues: z
        .array(
          z.looseObject({
            severity: z.enum(['error', 'warning', 'info']).describe('How serious the finding is.'),
            code: z.string().describe('Stable machine-readable code of the finding, for example "overlap".'),
            message: z.string().describe('Plain-language explanation.'),
            entityIds: z.array(z.string()).describe('Ids of the entities involved.'),
          }),
        )
        .describe('The findings; empty when the drawing passed every check that ran.'),
      checked: z.array(z.string()).describe('Names of the checks that ran.'),
    }),
  }),
} as const;

export const viewTools = {
  zoom_extents: defineTool('zoom_extents', {
    title: 'Zoom extents',
    description: [
      'Fits the whole drawing in the CAD window so the user sees everything. Changes only the view, never the',
      'drawing. Use it after creating a plan. Takes no arguments.',
    ],
    category: 'view',
    risk: 'read',
    input: noArguments,
    output: viewOutput,
  }),

  zoom_to_entities: defineTool('zoom_to_entities', {
    title: 'Zoom to entities',
    description: [
      'Zooms the CAD window so the given entities fill the view, to show the user what you just created or are',
      'talking about. Changes only the view, never the drawing.',
    ],
    category: 'view',
    risk: 'read',
    input: z.strictObject({
      ids: idList('Ids of the entities to bring into view (1 to 500).'),
    }),
    output: viewOutput,
  }),

  highlight_entities: defineTool('highlight_entities', {
    title: 'Highlight entities',
    description: [
      'Temporarily highlights the given entities in the CAD window so the user can see which objects you mean (for',
      'example before asking for confirmation). Replaces any previous highlight and never changes the drawing.',
      "Some CAD applications show the highlight by selecting the entities (CAD Studio does), which replaces the user's",
      'current selection. Remove it with clear_highlight.',
    ],
    category: 'view',
    risk: 'read',
    input: z.strictObject({
      ids: idList('Ids of the entities to highlight (1 to 500).'),
    }),
    output: viewOutput,
  }),

  clear_highlight: defineTool('clear_highlight', {
    title: 'Clear highlight',
    description: [
      'Removes the temporary highlight set by highlight_entities. Changes nothing else. Takes no arguments.',
    ],
    category: 'view',
    risk: 'read',
    input: noArguments,
    output: viewOutput,
  }),
} as const;

export const documentTools = {
  undo_last_operation: defineTool('undo_last_operation', {
    title: 'Undo last operation',
    description: [
      'Undoes the most recent operation in the drawing, exactly like the user pressing Undo once (each tool call',
      'is one undo step). Use it to revert your own last change when it turned out wrong. Returns { undone } with',
      'a description of the operation that was undone, or null when there was nothing to undo. Takes no arguments.',
    ],
    category: 'document',
    risk: 'write',
    input: noArguments,
    output: z.looseObject({
      undone: z.string().nullable().describe('What was undone, or null when the undo history was empty.'),
    }),
  }),

  save_document: defineTool('save_document', {
    title: 'Save document',
    description: [
      'Saves the drawing to its existing file, overwriting it. DESTRUCTIVE to the file on disk: the user is always',
      'asked to approve it. Call it only when the user asks to save. A drawing that was never saved has no file',
      'path: depending on the CAD application the call then either asks the user where to save it (CAD Studio opens',
      'its Save As dialog; if the user cancels, the call fails) or returns { path: null, saved: false } and the user',
      'must use Save As in the CAD application. Returns { path, saved }. Takes no arguments.',
    ],
    category: 'document',
    risk: 'destructive',
    input: noArguments,
    output: z.looseObject({
      path: z.string().nullable().describe('File the drawing was saved to, or null when it has no file yet.'),
      saved: z.boolean().describe('True when the file was written.'),
    }),
  }),
} as const;
