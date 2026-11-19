// Public types and constants of the CAD Copilot agent (see docs/architecture/copilot.md).
// Type-only towards the Anthropic SDK and the protocol package, so importing this module is free.
import type {
  BetaContentBlock,
  BetaMessageParam,
} from '@anthropic-ai/sdk/resources/beta/messages/messages';
import type { DocumentInfo, ToolResult } from '@cadstudio/cad-protocol';

// ---------------------------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------------------------

/** Claude models the user can pick (exact API ids, no date suffixes). */
export type ModelId = 'claude-opus-5-5' | 'claude-fable-5-2' | 'claude-sonnet-5-5' | 'claude-haiku-5-5';
/** Every selectable model, default first. */
export const MODEL_IDS: readonly ModelId[] = [
  'claude-opus-5-5',
  'claude-fable-5-2',
  'claude-sonnet-5-5',
  'claude-haiku-5-5',
];
/** The model used unless the user picks another one. */
export const DEFAULT_MODEL: ModelId = 'claude-opus-5-5';

/** How much effort (thinking depth, thoroughness) the model puts into a turn. */
export type Effort = 'low' | 'medium' | 'high' | 'xhigh' | 'max';
/** Every effort level, lowest first. */
export const EFFORTS: readonly Effort[] = ['low', 'medium', 'high', 'xhigh', 'max'];
/** The effort used unless the user picks another one. It is always sent explicitly. */
export const DEFAULT_EFFORT: Effort = 'high';

/**
 * ask-all: ask before every write; ask-destructive (default): ask only before destructive tools;
 * auto: never ask for writes (destructive tools still ask).
 */
export type ApprovalMode = 'ask-all' | 'ask-destructive' | 'auto';
/** The approval mode used unless the user picks another one. */
export const DEFAULT_APPROVAL_MODE: ApprovalMode = 'ask-destructive';

// ---------------------------------------------------------------------------------------------
// CAD side
// ---------------------------------------------------------------------------------------------

/** What the session needs from the CAD side (implemented by apps/copilot over a CadConnection). */
export interface CadToolExecutor {
  /** Human name of the connected CAD, e.g. 'CAD Studio' or 'AutoCAD 2026'. */
  cadName(): string;
  /** Catalog names the connected CAD supports (from HelloResult). */
  supportedTools(): readonly string[];
  /** Current drawing (for context and expectedRevision), or null when no drawing is open. */
  currentDocument(): DocumentInfo | null;
  /**
   * Runs one catalog tool. Rejects only for transport failures; CAD failures come back in the
   * ToolResult. `expectedRevision` (sent for write/destructive tools) is the drawing revision the
   * model last saw; the CAD refuses the call with a failed ToolResult when the drawing has changed
   * since, so the model re-inspects instead of acting on a stale picture.
   */
  execute(
    name: string,
    args: unknown,
    opts: { operationId: string; instruction: string; signal: AbortSignal; expectedRevision?: number },
  ): Promise<ToolResult>;
}

// ---------------------------------------------------------------------------------------------
// Questions, plan, attachments, usage
// ---------------------------------------------------------------------------------------------

export interface QuestionOption {
  label: string;
  description?: string;
  recommended?: boolean;
}
export interface Question {
  id: string;
  /** Short uppercase label, e.g. 'WALL THICKNESS'. */
  header: string;
  question: string;
  options: QuestionOption[];
  /** Whether the user may type a custom answer instead of choosing an option. */
  allowCustom: boolean;
}
export interface QuestionAnswer {
  questionId: string;
  /** The chosen option label, or the custom text. */
  answer: string;
}

export type TaskStatus = 'pending' | 'in_progress' | 'done' | 'failed';
export interface PlanTask {
  id: string;
  title: string;
  status: TaskStatus;
}

export interface Attachment {
  name: string;
  /** 'image/png' | 'image/jpeg' | 'image/webp' | 'image/gif' | 'application/pdf' | 'text/plain' ... */
  mediaType: string;
  dataBase64: string;
}

/** Token usage, summed over every model call of a turn. */
export interface TurnUsage {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

// ---------------------------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------------------------

export type SessionEvent =
  /** Streaming assistant text; `text` is the delta. */
  | { type: 'message.delta'; messageId: string; text: string }
  /** The assistant text block is complete; `text` is the full text. */
  | { type: 'message.done'; messageId: string; text: string }
  | { type: 'tool.started'; callId: string; name: string; title: string; summary: string; args: unknown }
  | { type: 'tool.finished'; callId: string; name: string; result: ToolResult | null; error?: string }
  | { type: 'question.asked'; requestId: string; questions: Question[] }
  | {
      type: 'approval.requested';
      requestId: string;
      callId: string;
      name: string;
      title: string;
      summary: string;
      risk: 'write' | 'destructive';
    }
  | { type: 'plan.updated'; tasks: PlanTask[] }
  /** stopReason: 'end_turn' | 'stopped' | 'refusal' | 'max_tokens' | 'error' | ... */
  | { type: 'turn.finished'; stopReason: string; usage: TurnUsage; model: string }
  | { type: 'error'; message: string; retryable: boolean };

// ---------------------------------------------------------------------------------------------
// Model transport (the seam between the session and the model)
// ---------------------------------------------------------------------------------------------

/** One tool offered to the model, independent of any provider's wire format. */
export interface ModelToolSpec {
  name: string;
  description: string;
  /** JSON Schema of the arguments (`type: "object"`). */
  inputSchema: Record<string, unknown>;
}

/** One model call: everything the model sees. `messages` is the session's append-only history. */
export interface ModelRequest {
  model: ModelId;
  effort: Effort;
  /** The stable system prompt (never contains dates, ids or other volatile text). */
  system: string;
  /** Tools in a deterministic order. */
  tools: readonly ModelToolSpec[];
  messages: readonly BetaMessageParam[];
}

/** Callbacks and cancellation for one model call. */
export interface ModelRunHooks {
  /** Aborted by CopilotSession.stop(); the transport must stop and reject promptly. */
  signal: AbortSignal;
  /**
   * Reports streamed assistant text. `blockIndex` identifies the text block inside the response
   * (defaults to 0), so that text before and after other blocks becomes separate messages.
   */
  onTextDelta(text: string, blockIndex?: number): void;
  /**
   * Called when the transport throws away what it has streamed so far and asks the model again
   * (the response could not be read). Text reported before this call is not part of the answer.
   */
  onRestart?(): void;
}

/** Why the model declined (only present when `stopReason` is 'refusal'). */
export interface ModelRefusal {
  category: string | null;
  explanation: string | null;
}

/** The complete assistant message of one model call. */
export interface ModelResponse {
  /** Content blocks exactly as the model produced them (thinking blocks included). */
  content: BetaContentBlock[];
  /** 'end_turn' | 'tool_use' | 'max_tokens' | 'pause_turn' | 'refusal' | ... (null is read as 'end_turn'). */
  stopReason: string | null;
  refusal?: ModelRefusal | null;
  usage: TurnUsage;
  /** The model that produced the message (a fallback model when one took over). */
  model: string;
}

/**
 * Produces one assistant message per request. `createAnthropicTransport` talks to Claude;
 * `createScriptedTransport` replays canned turns for end-to-end tests and offline demos.
 * `run` rejects when the call fails or is aborted; it never resolves with a partial message.
 */
export interface ModelTransport {
  readonly kind: string;
  run(request: ModelRequest, hooks: ModelRunHooks): Promise<ModelResponse>;
}
