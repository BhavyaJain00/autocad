/**
 * What each IPC channel does once the sender has been found trustworthy: validate the arguments,
 * then call the application. Kept apart from Electron's ipcMain so the validation of every
 * channel is unit-testable. No Electron imports.
 */
import type { QuestionAnswer } from '@cadstudio/copilot-agent';
import {
  COPILOT_IPC,
  type ActionResult,
  type ConnectorSummary,
  type CopilotSettings,
  type CopilotSnapshot,
  type PickedAttachment,
  type SettingsPatch,
} from '../shared/bridge';
import { checkApiKeyShape } from './credentials';
import { parseSettingsPatch } from './settings';
import {
  checkAnswers,
  checkConnectorId,
  checkDecision,
  checkExternalUrl,
  checkMessage,
  checkRequestId,
  type ApprovalDecision,
} from './validation';

/** The application behind the bridge. Every argument has been validated when these are called. */
export interface CopilotIpcApi {
  getSnapshot(): CopilotSnapshot;
  sendMessage(text: string, attachments: PickedAttachment[]): ActionResult;
  answerQuestion(requestId: string, answers: QuestionAnswer[]): void;
  resolveApproval(requestId: string, decision: ApprovalDecision): void;
  stop(): void;
  resetSession(): void;
  refreshConnectors(): Promise<ConnectorSummary[]>;
  connect(connectorId: string): Promise<ActionResult>;
  disconnect(): void;
  setDocked(docked: boolean): Promise<ActionResult>;
  currentSettings(): CopilotSettings;
  updateSettings(patch: SettingsPatch): CopilotSettings;
  /** `key` is a trimmed, plausible key, or null to remove the stored one. */
  setApiKey(key: string | null): Promise<ActionResult>;
  pickAttachments(): Promise<PickedAttachment[]>;
  /** `url` is a normalised https URL. */
  openExternal(url: string): void;
  minimizeWindow(): void;
  closeWindow(): void;
}

/** Handler of a request/response channel (ipcRenderer.invoke); receives the raw arguments. */
export type InvokeHandler = (args: readonly unknown[]) => unknown;
/** Handler of a one-way channel (ipcRenderer.send); receives the raw arguments. */
export type SendHandler = (args: readonly unknown[]) => void;

export interface CopilotIpcHandlers {
  readonly invoke: Readonly<Record<string, InvokeHandler>>;
  readonly send: Readonly<Record<string, SendHandler>>;
}

/** Channels the main process pushes on; the renderer never sends on them. */
export const PUSH_CHANNELS: readonly string[] = [
  COPILOT_IPC.timeline,
  COPILOT_IPC.connection,
  COPILOT_IPC.connectors,
  COPILOT_IPC.settings,
  COPILOT_IPC.busy,
];

/**
 * Builds the handler of every renderer-to-main channel. Invalid arguments never reach `api`:
 * request channels answer with `{ ok: false, message }` (or the unchanged settings), one-way
 * channels drop the message and log why.
 */
export function createIpcHandlers(api: CopilotIpcApi, log: (message: string) => void = () => undefined): CopilotIpcHandlers {
  const invoke: Record<string, InvokeHandler> = {
    [COPILOT_IPC.getSnapshot]: () => api.getSnapshot(),

    [COPILOT_IPC.sendMessage]: ([text, attachments]): ActionResult => {
      const message = checkMessage(text, attachments);
      if (!message.ok) return message;
      return api.sendMessage(message.text, message.attachments);
    },

    [COPILOT_IPC.refreshConnectors]: () => api.refreshConnectors(),

    [COPILOT_IPC.connect]: ([connectorId]): ActionResult | Promise<ActionResult> => {
      const id = checkConnectorId(connectorId);
      if (id === null) return { ok: false, message: 'That is not a CAD application CAD Copilot can connect to.' };
      return api.connect(id);
    },

    [COPILOT_IPC.setDocked]: ([docked]): ActionResult | Promise<ActionResult> => {
      if (typeof docked !== 'boolean') return { ok: false, message: 'The dock state must be true or false.' };
      return api.setDocked(docked);
    },

    [COPILOT_IPC.updateSettings]: ([patch]): CopilotSettings => {
      const parsed = parseSettingsPatch(patch);
      if (!parsed.ok) {
        log(`Ignored a settings change: ${parsed.error}`);
        return api.currentSettings();
      }
      return api.updateSettings(parsed.value);
    },

    [COPILOT_IPC.setApiKey]: ([key]): ActionResult | Promise<ActionResult> => {
      if (key === null) return api.setApiKey(null);
      const checked = checkApiKeyShape(key);
      if (!checked.ok) return { ok: false, message: checked.message };
      return api.setApiKey(checked.key);
    },

    [COPILOT_IPC.pickAttachments]: () => api.pickAttachments(),
  };

  const send: Record<string, SendHandler> = {
    [COPILOT_IPC.answerQuestion]: ([requestId, answers]) => {
      const id = checkRequestId(requestId);
      const checked = checkAnswers(answers);
      if (id === null || checked === null) {
        log('Ignored an invalid answer to a question.');
        return;
      }
      api.answerQuestion(id, checked);
    },

    [COPILOT_IPC.resolveApproval]: ([requestId, decision]) => {
      const id = checkRequestId(requestId);
      const checked = checkDecision(decision);
      if (id === null || checked === null) {
        log('Ignored an invalid approval decision.');
        return;
      }
      api.resolveApproval(id, checked);
    },

    [COPILOT_IPC.stop]: () => api.stop(),
    [COPILOT_IPC.resetSession]: () => api.resetSession(),
    [COPILOT_IPC.disconnect]: () => api.disconnect(),

    [COPILOT_IPC.openExternal]: ([url]) => {
      const href = checkExternalUrl(url);
      if (href === null) {
        log('Ignored a request to open something that is not an https URL.');
        return;
      }
      api.openExternal(href);
    },

    [COPILOT_IPC.minimizeWindow]: () => api.minimizeWindow(),
    [COPILOT_IPC.closeWindow]: () => api.closeWindow(),
  };

  return { invoke, send };
}
