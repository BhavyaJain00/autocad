/** Every catalog tool name (see copilot.md "Tool catalog"), in catalog order. */
export const TOOL_NAMES = [
  'get_drawing_info',
  'list_layers',
  'inspect_entities',
  'get_selected_objects',
  'find_objects',
  'inspect_room',
  'get_drawing_revision',
  'create_line',
  'create_polyline',
  'create_rectangle',
  'create_circle',
  'create_arc',
  'create_text',
  'create_entities',
  'create_wall',
  'create_room',
  'create_door',
  'create_window',
  'create_staircase',
  'create_column',
  'create_plot_boundary',
  'create_floor_plan',
  'move_entities',
  'copy_entities',
  'rotate_entities',
  'scale_entities',
  'mirror_entities',
  'update_entity_properties',
  'move_room',
  'resize_room',
  'delete_entities',
  'add_dimension',
  'add_room_labels',
  'create_layer',
  'set_current_layer',
  'set_layer_state',
  'validate_drawing',
  'zoom_extents',
  'zoom_to_entities',
  'highlight_entities',
  'clear_highlight',
  'undo_last_operation',
  'save_document',
] as const;

export type ToolName = (typeof TOOL_NAMES)[number];

const TOOL_NAME_SET: ReadonlySet<string> = new Set<string>(TOOL_NAMES);

/** True when `name` is a catalog tool name. */
export function isToolName(name: unknown): name is ToolName {
  return typeof name === 'string' && TOOL_NAME_SET.has(name);
}
