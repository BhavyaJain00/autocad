// CAD Copilot shared contract (docs/architecture/copilot.md): tool catalog, bridge message types,
// JSON-RPC / NDJSON framing. Browser-safe: nothing here imports Node modules. The Node-only
// transport (named pipes, discovery files) lives in ./node.ts, exported as
// '@cadstudio/cad-protocol/node'.

export {
  BRIDGE_METHODS,
  BRIDGE_NOTIFICATIONS,
  BRIDGE_PROTOCOL_VERSION,
  BridgeError,
  BridgeErrorCode,
  CAD_KINDS,
  TOOL_CATEGORIES,
  UNITS,
  failedToolResult,
  isBridgeError,
} from './core';
export type {
  BridgeNotificationParams,
  CadKind,
  ConnectorAdvert,
  DocumentInfo,
  HelloResult,
  Point2,
  ToolCallParams,
  ToolCategory,
  ToolDefinition,
  ToolResult,
  ToolRisk,
  Unit,
  WindowBounds,
} from './core';

export { TOOL_NAMES, isToolName } from './tools/names';
export type { ToolName } from './tools/names';

export {
  MAX_VALIDATION_ERRORS,
  TOOLS,
  TOOL_CATALOG,
  formatValidationIssues,
  getTool,
  toolJsonSchema,
  validateToolArguments,
} from './tools/catalog';
export type { ToolArguments, ToolInput, ToolOutput, ToolValidation } from './tools/catalog';

export {
  DEFAULT_INSPECT_LIMIT,
  GEOMETRY_ENTITY_TYPES,
  MAX_BATCH_ENTITIES,
  MAX_ID_COUNT,
  MAX_ID_LENGTH,
  MAX_INSPECT_LIMIT,
  MAX_LAYER_NAME_LENGTH,
  MAX_POLYLINE_POINTS,
  MAX_TEXT_LENGTH,
  ROOM_TYPES,
  entityRefSchema,
  entitySummarySchema,
  layerSummarySchema,
  roomDetailsSchema,
  unitSchema,
} from './tools/shared';
export type {
  CatalogTool,
  EntityGeometryByType,
  EntityRef,
  EntitySummary,
  GeometryEntityType,
  LayerSummary,
  RoomDetails,
  RoomType,
} from './tools/shared';

export { batchEntitySchema } from './tools/geometry';
export type { BatchEntity } from './tools/geometry';

export { describeToolCall } from './describe';

export {
  authParamsSchema,
  connectorAdvertSchema,
  documentInfoSchema,
  helloResultSchema,
  setWindowBoundsParamsSchema,
  toolCallParamsSchema,
  toolResultSchema,
  windowBoundsSchema,
} from './messages';
export type { AuthParams } from './messages';

export {
  NDJSON_MAX_LINE_LENGTH,
  NdjsonDecoder,
  encodeNdjson,
  isRpcFailure,
  isRpcNotification,
  isRpcRequest,
  isRpcSuccess,
} from './rpc';
export type { RpcFailure, RpcMessage, RpcNotification, RpcRequest, RpcSuccess } from './rpc';
