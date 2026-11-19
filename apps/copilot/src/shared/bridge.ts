/**
 * CAD Copilot bridge contract between the Electron main process (which owns the CopilotSession,
 * the CAD connection, docking and settings) and the React renderer (a pure view).
 *
 * The preload script exposes an implementation of {@link CopilotBridge} as `window.copilot`.
 * This module must stay free of Node and Electron imports and may only use `import type` from the
 * agent/protocol packages (they are erased at build time; the renderer never bundles the SDK).
 *
 * BINDING CONTRACT: the main-process agent and the UI agent both code against this file.
 */
import type { CadKind, DocumentInfo } from '@cadstudio/cad-protocol';
import type {
  ApprovalMode,
  Attachment,
  Effort,
  ModelId,
  QuestionAnswer,
  SessionEvent,
} from '@cadstudio/copilot-agent';

export const MODEL_CHOICES: readonly { id: ModelId; label: string; hint: string }[] = [
  { id: 'claude-opus-5-5', label: 'Claude Opus 5.5', hint: 'Recommended for CAD work' },
  { id: 'claude-fable-5-2', label: 'Claude Fable 5.2', hint: 'Most capable; slower and costs more' },
  { id: 'claude-sonnet-5-5', label: 'Claude Sonnet 5.5', hint: 'Faster, lower cost' },
  { id: 'claude-haiku-5-5', label: 'Claude Haiku 5.5', hint: 'Fastest, lowest cost' },
];

export const EFFORT_CHOICES: readonly { id: Effort; label: string }[] = [
  { id: 'low', label: 'Low' },
  { id: 'medium', label: 'Medium' },
  { id: 'high', label: 'High' },
  { id: 'xhigh', label: 'Extra high' },
  { id: 'max', label: 'Max' },
];

export const APPROVAL_CHOICES: readonly { id: ApprovalMode; label: string; hint: string }[] = [
  { id: 'ask-all', label: 'Ask before every change', hint: 'Approve each drawing change' },
  { id: 'ask-destructive', label: 'Ask before deleting or saving', hint: 'Other changes run automatically' },
  { id: 'auto', label: 'Run automatically', hint: 'Only deleting and saving still ask' },
];

/** Panel width limits in device-independent pixels. */
export const PANEL_WIDTH_MIN = 380;
export const PANEL_WIDTH_MAX = 760;
export const PANEL_WIDTH_DEFAULT = 460;

/** Attachment limits enforced by the main process. */
export const ATTACHMENT_MAX_COUNT = 8;
export const ATTACHMENT_MAX_BYTES = 10 * 1024 * 1024;

/** A running CAD application the copilot can connect to. */
export interface ConnectorSummary {
  id: string;
  cad: CadKind;
  /** e.g. 'CAD Studio' or 'AutoCAD 2026'. */
  displayName: string;
  cadVersion: string;
  pid: number;
}

export type ConnectionState =
  | { status: 'disconnected' }
  | { status: 'connecting'; connectorId: string }
  | {
      status: 'connected';
      connector: ConnectorSummary;
      document: DocumentInfo | null;
      supportedTools: string[];
      /** True while the copilot window is docked beside the CAD window. */
      docked: boolean;
    }
  | { status: 'error'; message: string };

export interface CopilotSettings {
  model: ModelId;
  effort: Effort;
  approvalMode: ApprovalMode;
  panelWidth: number;
  alwaysOnTop: boolean;
  /** Dock beside the CAD window automatically after connecting. */
  autoDock: boolean;
  /** Connect automatically when exactly one CAD application is running. */
  autoConnect: boolean;
  /** Whether an API key is stored. The key itself never reaches the renderer. */
  hasApiKey: boolean;
  /** True when a scripted test model replaces Claude (end-to-end tests / offline demo). */
  testModel: boolean;
}

export type SettingsPatch = Partial<
  Pick<CopilotSettings, 'model' | 'effort' | 'approvalMode' | 'panelWidth' | 'alwaysOnTop' | 'autoDock' | 'autoConnect'>
>;

export interface PickedAttachment extends Attachment {
  sizeBytes: number;
}

export interface AttachmentInfo {
  name: string;
  mediaType: string;
  sizeBytes: number;
}

/**
 * Everything shown in the conversation, in order. The main process stamps each entry; the renderer
 * reduces the list into the transcript (messages, question cards, CAD activity, task progress).
 */
export type TimelineEntry = { seq: number; at: string } & (
  | SessionEvent
  | { type: 'user.message'; text: string; attachments: AttachmentInfo[] }
  | { type: 'question.answered'; requestId: string; answers: QuestionAnswer[] }
  | { type: 'approval.resolved'; requestId: string; decision: 'approve' | 'reject' | 'approve-all' }
  | { type: 'turn.stopped' }
  | { type: 'session.reset' }
);

/** Everything the renderer needs at startup or after a reload. */
export interface CopilotSnapshot {
  appVersion: string;
  settings: CopilotSettings;
  connection: ConnectionState;
  connectors: ConnectorSummary[];
  busy: boolean;
  /**
   * The timeline since the last reset. Streaming deltas are coalesced: an unfinished assistant
   * message appears as one 'message.delta' entry holding the text so far.
   */
  timeline: TimelineEntry[];
}

export type ActionResult = { ok: true } | { ok: false; message: string };

export interface CopilotBridge {
  getSnapshot(): Promise<CopilotSnapshot>;
  /** Live timeline entries (including every streaming delta). Returns an unsubscribe function. */
  onTimeline(listener: (entry: TimelineEntry) => void): () => void;
  onConnection(listener: (connection: ConnectionState) => void): () => void;
  onConnectors(listener: (connectors: ConnectorSummary[]) => void): () => void;
  onSettings(listener: (settings: CopilotSettings) => void): () => void;
  onBusy(listener: (busy: boolean) => void): () => void;

  /** Starts a turn. Resolves as soon as the turn is accepted (not when it finishes). */
  sendMessage(text: string, attachments: PickedAttachment[]): Promise<ActionResult>;
  answerQuestion(requestId: string, answers: QuestionAnswer[]): void;
  resolveApproval(requestId: string, decision: 'approve' | 'reject' | 'approve-all'): void;
  stop(): void;
  /** Stops any running turn and starts a new conversation. */
  resetSession(): void;

  refreshConnectors(): Promise<ConnectorSummary[]>;
  connect(connectorId: string): Promise<ActionResult>;
  disconnect(): void;
  /** Docks beside the CAD window (resizing it) or undocks (restoring the CAD window's bounds). */
  setDocked(docked: boolean): Promise<ActionResult>;

  updateSettings(patch: SettingsPatch): Promise<CopilotSettings>;
  /** Stores (after verifying) or, with null, removes the API key. */
  setApiKey(key: string | null): Promise<ActionResult>;
  /** Native file picker for images, PDFs and text files; enforces the attachment limits. */
  pickAttachments(): Promise<PickedAttachment[]>;
  /** Opens a https URL in the default browser (used for the "get an API key" link). */
  openExternal(url: string): void;

  minimizeWindow(): void;
  closeWindow(): void;
}

/** IPC channel names used between the preload script and the main process. */
export const COPILOT_IPC = {
  getSnapshot: 'copilot:get-snapshot',
  timeline: 'copilot:timeline',
  connection: 'copilot:connection',
  connectors: 'copilot:connectors',
  settings: 'copilot:settings',
  busy: 'copilot:busy',
  sendMessage: 'copilot:send-message',
  answerQuestion: 'copilot:answer-question',
  resolveApproval: 'copilot:resolve-approval',
  stop: 'copilot:stop',
  resetSession: 'copilot:reset-session',
  refreshConnectors: 'copilot:refresh-connectors',
  connect: 'copilot:connect',
  disconnect: 'copilot:disconnect',
  setDocked: 'copilot:set-docked',
  updateSettings: 'copilot:update-settings',
  setApiKey: 'copilot:set-api-key',
  pickAttachments: 'copilot:pick-attachments',
  openExternal: 'copilot:open-external',
  minimizeWindow: 'copilot:window-minimize',
  closeWindow: 'copilot:window-close',
} as const;

declare global {
  interface Window {
    copilot: CopilotBridge;
  }
}
