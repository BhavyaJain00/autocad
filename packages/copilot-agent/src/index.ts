// CAD Copilot agent: the Claude-powered tool-use loop (clarifying questions, task plan, approvals,
// cancellation). Runs in the copilot's Electron main process (Node). See docs/architecture/copilot.md.

export {
  DEFAULT_APPROVAL_MODE,
  DEFAULT_EFFORT,
  DEFAULT_MODEL,
  EFFORTS,
  MODEL_IDS,
} from './types';
export type {
  ApprovalMode,
  Attachment,
  CadToolExecutor,
  Effort,
  ModelId,
  ModelRefusal,
  ModelRequest,
  ModelResponse,
  ModelRunHooks,
  ModelToolSpec,
  ModelTransport,
  PlanTask,
  Question,
  QuestionAnswer,
  QuestionOption,
  SessionEvent,
  TaskStatus,
  TurnUsage,
} from './types';

export {
  CopilotSession,
  DEFAULT_MAX_MODEL_CALLS,
  echoableContent,
  requiresApproval,
  withoutThinking,
} from './session';
export type { ApprovalDecision, SessionOptions } from './session';

export { SYSTEM_PROMPT, buildContextBlock } from './prompt';
export type { TurnContext } from './prompt';

export {
  ASK_USER_TOOL,
  CATALOG_TOOL_SOURCE,
  LOCAL_TOOL_NAMES,
  LOCAL_TOOL_SPECS,
  MAX_PLAN_TASKS,
  MAX_QUESTIONS,
  MAX_QUESTION_OPTIONS,
  MIN_QUESTION_OPTIONS,
  UPDATE_PLAN_TOOL,
  buildModelTools,
  createToolSource,
  isLocalTool,
  offeredCadToolNames,
  parseAskUser,
  parseUpdatePlan,
} from './tools';
export type { LocalToolParse, ToolArgumentCheck, ToolSource } from './tools';

export {
  CANCELLED_MESSAGE,
  CANCELLED_RUNNING_MESSAGE,
  DECLINED_MESSAGE,
  MAX_TOOL_RESULT_CHARS,
  errorResultText,
  serializeForModel,
  serializeToolResult,
} from './tool-result';

export { MAX_TEXT_ATTACHMENT_CHARS, mapAttachments } from './attachments';
export type { AttachmentMapping } from './attachments';

export { describeModelError } from './errors';
export type { ErrorDescription } from './errors';

export {
  FALLBACK_BETA,
  MAX_OUTPUT_TOKENS,
  buildAnthropicRequest,
  createAnthropicTransport,
  supportsServerFallback,
  usageFromMessage,
  verifyApiKey,
} from './transport-anthropic';
export type {
  AnthropicClientLike,
  AnthropicStreamLike,
  AnthropicTransportOptions,
  ModelsClientLike,
} from './transport-anthropic';

export {
  MAX_SCRIPT_DELAY_MS,
  SCRIPTED_MODEL_NAME,
  SCRIPT_FINISHED_TEXT,
  createScriptedTransport,
  parseModelScript,
} from './transport-scripted';
export type { ModelScript, ScriptedTurn } from './transport-scripted';
