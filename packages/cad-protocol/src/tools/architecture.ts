// Architectural tools (write): walls, rooms, openings, stairs, columns, plot boundary, floor plans.
import { z } from 'zod';
import {
  createdEntitiesOutput,
  defineTool,
  degrees,
  entityRefSchema,
  id,
  nonNegative,
  point,
  pointOutput,
  positive,
  roomDetailsSchema,
  roomTypeSchema,
  unitField,
} from './shared';

const RESULT_NOTE = 'Returns { entities: [{ id, type }] } listing every object that was created.';
const UNIT_NOTE = 'All lengths and coordinates are in "unit" (default: the drawing unit).';

const roomName = (description: string) => z.string().min(1).max(100).describe(description);

export const architectureTools = {
  create_wall: defineTool('create_wall', {
    title: 'Create wall',
    description: [
      'Draws one straight wall along the centre line from "start" to "end" with the given "thickness". "kind"',
      'classifies it (external, internal or partition); "justification" shifts the wall body to the left or right',
      'of the start-to-end line instead of centring it. Walls that meet are joined at their corners. Use it for',
      'individual walls; use create_room for a whole rectangular room and create_floor_plan for a complete layout.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'architecture',
    risk: 'write',
    input: z.strictObject({
      start: point('Start of the wall centre line.'),
      end: point('End of the wall centre line (must differ from the start).'),
      thickness: positive('Wall thickness (> 0), for example 230 mm / 9 in external, 115 mm / 4.5 in internal.'),
      kind: z
        .enum(['external', 'internal', 'partition'])
        .optional()
        .describe(
          'Wall class: "external" (building envelope), "internal" (between rooms) or "partition" (thin divider). ' +
            'Default "internal".',
        ),
      justification: z
        .enum(['center', 'left', 'right'])
        .optional()
        .describe(
          'Where the wall body sits relative to the start-to-end line: "center" (default), "left" or "right" as ' +
            'seen when looking from start to end.',
        ),
      height: positive('Wall height (> 0), stored as data for schedules; it does not change the 2D plan.').optional(),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),

  create_room: defineTool('create_room', {
    title: 'Create room',
    description: [
      'Creates one rectangular room from its INTERIOR size: "width" along X and "length" along Y, with its',
      'lower-left interior corner at "origin". By default it also builds the four enclosing walls (outside the',
      'interior, "wallThickness" thick) and a name label in the middle. When "origin" is omitted the room is',
      'placed clear of existing geometry. Use it for a single room; use create_floor_plan to lay out several rooms',
      'on a plot. Add openings afterwards with create_door and create_window.',
      UNIT_NOTE,
      'Returns { entities: [{ id, type }], room } where room has the room id, size, area and wall ids.',
    ],
    category: 'architecture',
    risk: 'write',
    input: z.strictObject({
      name: roomName('Room name shown on the label, for example "Master Bedroom" (1 to 100 characters).'),
      roomType: roomTypeSchema.optional().describe('Kind of room; drives defaults and validation. Default "other".'),
      width: positive('Interior clear size along X (> 0).'),
      length: positive('Interior clear size along Y (> 0).'),
      origin: point('Lower-left INTERIOR corner of the room.')
        .optional()
        .describe('Lower-left INTERIOR corner of the room. Omit to place the room clear of existing geometry.'),
      wallThickness: positive('Thickness of the enclosing walls (> 0). Omit for the CAD\'s default.').optional(),
      createWalls: z
        .boolean()
        .optional()
        .describe('False to create only the room outline without walls. Default true.'),
      addLabel: z.boolean().optional().describe('False to skip the room name label. Default true.'),
      unit: unitField,
    }),
    output: z.looseObject({
      entities: z.array(entityRefSchema).describe('Every created object (room, walls, label), in creation order.'),
      room: roomDetailsSchema.optional().describe('Details of the new room.'),
    }),
  }),

  create_door: defineTool('create_door', {
    title: 'Create door',
    description: [
      'Places a door of the given "width" in a wall, centred at the point of the wall nearest to "position", and',
      'cuts the opening. Give "wallId" to choose the wall explicitly; otherwise the nearest wall to "position" is',
      'used. "swing" sets the hinge side and "opensInto" which side the leaf opens to. Fails (with an explanation)',
      'when no wall is near or the door does not fit in the wall.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'architecture',
    risk: 'write',
    input: z.strictObject({
      position: point('Centre of the door, on or near the wall that receives it.'),
      width: positive('Clear door width (> 0), for example 900 mm / 3 ft.'),
      wallId: id('Id of the wall to put the door in. Omit to use the wall nearest to "position".').optional(),
      doorType: z
        .enum(['single', 'double', 'sliding', 'main_entrance'])
        .optional()
        .describe('Door style: "single" (default), "double", "sliding" or "main_entrance".'),
      swing: z
        .enum(['left', 'right'])
        .optional()
        .describe('Hinge side as seen from the side the door opens into: "left" (default) or "right".'),
      opensInto: z
        .enum(['inside', 'outside'])
        .optional()
        .describe('Side the leaf opens to: "inside" the room (default) or "outside".'),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),

  create_window: defineTool('create_window', {
    title: 'Create window',
    description: [
      'Places a window of the given "width" in a wall, centred at the point of the wall nearest to "position".',
      'Give "wallId" to choose the wall explicitly; otherwise the nearest wall is used. "sillHeight" and "height"',
      'are stored as data for schedules. Fails (with an explanation) when no wall is near or the window does not',
      'fit in the wall.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'architecture',
    risk: 'write',
    input: z.strictObject({
      position: point('Centre of the window, on or near the wall that receives it.'),
      width: positive('Window width along the wall (> 0).'),
      wallId: id('Id of the wall to put the window in. Omit to use the wall nearest to "position".').optional(),
      windowType: z
        .enum(['fixed', 'sliding', 'casement'])
        .optional()
        .describe('Window style: "fixed", "sliding" (default) or "casement".'),
      sillHeight: nonNegative('Height of the sill above the floor (>= 0).').optional(),
      height: positive('Height of the window opening (> 0).').optional(),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),

  create_staircase: defineTool('create_staircase', {
    title: 'Create staircase',
    description: [
      'Draws a straight staircase in plan: a flight "width" wide and "length" long (the run), starting at',
      '"origin" and climbing in "direction" (degrees counter-clockwise from +X; default 90 = up the page), with',
      'tread lines and an up arrow. "origin" is the lower-left corner of the flight when direction is 90.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'architecture',
    risk: 'write',
    input: z.strictObject({
      origin: point('Start corner of the flight (the lower-left corner when "direction" is 90).'),
      width: positive('Width of the flight, across the direction of travel (> 0).'),
      length: positive('Run of the flight, along the direction of travel (> 0).'),
      direction: degrees(
        'Direction of travel going up, in degrees counter-clockwise from +X. Default 90 (up the page).',
      ).default(90),
      steps: z
        .number()
        .int()
        .min(2)
        .max(60)
        .optional()
        .describe('Number of treads, 2 to 60. Omit to derive it from the run and a comfortable tread depth.'),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),

  create_column: defineTool('create_column', {
    title: 'Create column',
    description: [
      'Places one structural column centred at "center". A "rectangular" column REQUIRES "width" (along X) and',
      '"depth" (along Y) and may be rotated; a "circular" column REQUIRES "diameter". Giving the wrong size fields',
      'for the shape is rejected.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'architecture',
    risk: 'write',
    input: z
      .strictObject({
        center: point('Centre of the column.'),
        shape: z.enum(['rectangular', 'circular']).describe('Cross-section: "rectangular" or "circular".'),
        width: positive(
          'Size along X before rotation (> 0). Required for a rectangular column; not allowed for a circular one.',
        ).optional(),
        depth: positive(
          'Size along Y before rotation (> 0). Required for a rectangular column; not allowed for a circular one.',
        ).optional(),
        diameter: positive(
          'Diameter (> 0). Required for a circular column; not allowed for a rectangular one.',
        ).optional(),
        rotation: degrees(
          'Rotation in degrees, counter-clockwise, about the centre (rectangular columns). Default 0.',
        ).optional(),
        unit: unitField,
      })
      .check((ctx) => {
        const value = ctx.value;
        const problem = (path: string, message: string): void => {
          ctx.issues.push({ code: 'custom', message, path: [path], input: value });
        };
        if (value.shape === 'rectangular') {
          if (value.width === undefined) problem('width', 'A rectangular column needs "width"');
          if (value.depth === undefined) problem('depth', 'A rectangular column needs "depth"');
          if (value.diameter !== undefined) {
            problem('diameter', 'A rectangular column takes "width" and "depth", not "diameter"');
          }
        } else {
          if (value.diameter === undefined) problem('diameter', 'A circular column needs "diameter"');
          if (value.width !== undefined) problem('width', 'A circular column takes "diameter", not "width"');
          if (value.depth !== undefined) problem('depth', 'A circular column takes "diameter", not "depth"');
        }
      }),
    output: createdEntitiesOutput,
  }),

  create_plot_boundary: defineTool('create_plot_boundary', {
    title: 'Create plot boundary',
    description: [
      'Draws the rectangular site (plot) boundary, "width" along X and "length" along Y, from its lower-left',
      '"origin" (default 0,0), and optionally the setback lines inside it. "front" is the bottom edge (minimum Y,',
      'the road side), "back" the top edge, "left" and "right" as seen on the page. Use it at the start of a house',
      'plan so the building can be placed inside the buildable area.',
      UNIT_NOTE,
      RESULT_NOTE,
    ],
    category: 'architecture',
    risk: 'write',
    input: z.strictObject({
      width: positive('Plot size along X (> 0).'),
      length: positive('Plot size along Y (> 0).'),
      origin: point('Lower-left corner of the plot.')
        .optional()
        .describe('Lower-left corner of the plot. Omit for (0, 0).'),
      setbacks: z
        .strictObject({
          front: nonNegative('Setback from the front (bottom, minimum Y) edge (>= 0).').optional(),
          back: nonNegative('Setback from the back (top, maximum Y) edge (>= 0).').optional(),
          left: nonNegative('Setback from the left (minimum X) edge (>= 0).').optional(),
          right: nonNegative('Setback from the right (maximum X) edge (>= 0).').optional(),
        })
        .optional()
        .describe('Distances from each plot edge to the buildable area; omitted sides have no setback line.'),
      unit: unitField,
    }),
    output: createdEntitiesOutput,
  }),

  create_floor_plan: defineTool('create_floor_plan', {
    title: 'Create floor plan',
    description: [
      'Lays out a complete single-floor plan on a rectangular plot: places 1 to 30 rooms without overlaps inside',
      'the plot, honouring requested sizes, minimum areas and adjacency wishes where possible, then draws the',
      'external and internal walls, the rooms and their labels. Rooms without a size are sized from their type and',
      '"minArea". Set "previewOnly" to true to get the proposed layout WITHOUT drawing anything (show it to the user',
      'or check "issues" first), then call again without it to draw. "entranceSide" is the plot side of the main',
      'entrance (north = +Y, the top of the page).',
      'All lengths are in "unit" (default: the drawing unit) and areas in that unit squared.',
      'Returns { layout: [{ name, roomType, origin, width, length, area }], issues: string[], created: boolean };',
      '"issues" lists anything that could not be satisfied. No building-code compliance is checked.',
    ],
    category: 'architecture',
    risk: 'write',
    input: z.strictObject({
      plot: z
        .strictObject({
          width: positive('Plot size along X, east-west (> 0).'),
          length: positive('Plot size along Y, north-south (> 0).'),
        })
        .describe('The rectangular plot (or buildable area) the plan must fit in.'),
      rooms: z
        .array(
          z.strictObject({
            name: roomName('Room name, unique within the plan, for example "Bedroom 2" (1 to 100 characters).'),
            roomType: roomTypeSchema.describe('Kind of room, for example "bedroom", "kitchen" or "toilet".'),
            width: positive('Wanted interior size along X (> 0). Omit to let the planner choose.').optional(),
            length: positive('Wanted interior size along Y (> 0). Omit to let the planner choose.').optional(),
            minArea: positive('Smallest acceptable interior area, in square units of the call (> 0).').optional(),
            adjacentTo: z
              .array(roomName('Name of another room in this plan.'))
              .max(30)
              .optional()
              .describe('Names of rooms (from this list) that this room should share a wall with.'),
          }),
        )
        .min(1)
        .max(30)
        .describe('The rooms to place, 1 to 30, most important first.'),
      entranceSide: z
        .enum(['north', 'south', 'east', 'west'])
        .optional()
        .describe('Plot side of the main entrance: "north" (+Y, top), "south" (default), "east" (+X) or "west".'),
      externalWallThickness: positive('Thickness of the external walls (> 0). Omit for the CAD\'s default.').optional(),
      internalWallThickness: positive('Thickness of the internal walls (> 0). Omit for the CAD\'s default.').optional(),
      previewOnly: z
        .boolean()
        .optional()
        .describe('True to compute and return the layout without drawing anything. Default false.'),
      unit: unitField,
    }),
    output: z.looseObject({
      layout: z
        .array(
          z.looseObject({
            name: z.string().describe('Room name.'),
            roomType: z.string().describe('Room type.'),
            origin: pointOutput.describe('Lower-left interior corner.'),
            width: z.number().describe('Interior size along X.'),
            length: z.number().describe('Interior size along Y.'),
            area: z.number().describe('Interior area in square units of the call.'),
          }),
        )
        .describe('The placed rooms.'),
      issues: z.array(z.string()).describe('Requirements that could not be met, in plain language.'),
      created: z.boolean().describe('True when the plan was drawn; false for a preview or a failed layout.'),
      entities: z.array(entityRefSchema).optional().describe('The created objects when "created" is true.'),
    }),
  }),
} as const;
