/**
 * Sandboxed preload script (bundled to dist/electron/preload.cjs). Exposes exactly one object to
 * the renderer, `window.copilot`, implementing the CopilotBridge contract over IPC. The page
 * never receives ipcRenderer, Node or Electron objects, and the API key never passes through
 * here in the main-to-renderer direction.
 */
import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron';
import type { QuestionAnswer } from '@cadstudio/copilot-agent';
import {
  COPILOT_IPC,
  type ActionResult,
  type ConnectionState,
  type ConnectorSummary,
  type CopilotBridge,
  type CopilotSettings,
  type CopilotSnapshot,
  type PickedAttachment,
  type SettingsPatch,
  type TimelineEntry,
} from '../shared/bridge';

/**
 * Subscribes a renderer listener to a push channel and returns the unsubscribe function. A
 * listener that throws is reported in the page's console and never breaks the other listeners.
 */
function subscribe<T>(channel: string, listener: (payload: T) => void): () => void {
  if (typeof listener !== 'function') throw new TypeError('The listener must be a function.');
  const handler = (_event: IpcRendererEvent, payload: T): void => {
    try {
      listener(payload);
    } catch (error) {
      console.error(`[copilot] A listener of ${channel} failed:`, error);
    }
  };
  ipcRenderer.on(channel, handler);
  let subscribed = true;
  return () => {
    if (!subscribed) return;
    subscribed = false;
    ipcRenderer.removeListener(channel, handler);
  };
}

const bridge: CopilotBridge = {
  getSnapshot: (): Promise<CopilotSnapshot> => ipcRenderer.invoke(COPILOT_IPC.getSnapshot),

  onTimeline: (listener: (entry: TimelineEntry) => void) => subscribe(COPILOT_IPC.timeline, listener),
  onConnection: (listener: (connection: ConnectionState) => void) => subscribe(COPILOT_IPC.connection, listener),
  onConnectors: (listener: (connectors: ConnectorSummary[]) => void) => subscribe(COPILOT_IPC.connectors, listener),
  onSettings: (listener: (settings: CopilotSettings) => void) => subscribe(COPILOT_IPC.settings, listener),
  onBusy: (listener: (busy: boolean) => void) => subscribe(COPILOT_IPC.busy, listener),

  sendMessage: (text: string, attachments: PickedAttachment[]): Promise<ActionResult> =>
    ipcRenderer.invoke(COPILOT_IPC.sendMessage, text, attachments),

  answerQuestion: (requestId: string, answers: QuestionAnswer[]): void => {
    ipcRenderer.send(COPILOT_IPC.answerQuestion, requestId, answers);
  },

  resolveApproval: (requestId: string, decision: 'approve' | 'reject' | 'approve-all'): void => {
    ipcRenderer.send(COPILOT_IPC.resolveApproval, requestId, decision);
  },

  stop: (): void => {
    ipcRenderer.send(COPILOT_IPC.stop);
  },

  resetSession: (): void => {
    ipcRenderer.send(COPILOT_IPC.resetSession);
  },

  refreshConnectors: (): Promise<ConnectorSummary[]> => ipcRenderer.invoke(COPILOT_IPC.refreshConnectors),

  connect: (connectorId: string): Promise<ActionResult> => ipcRenderer.invoke(COPILOT_IPC.connect, connectorId),

  disconnect: (): void => {
    ipcRenderer.send(COPILOT_IPC.disconnect);
  },

  setDocked: (docked: boolean): Promise<ActionResult> => ipcRenderer.invoke(COPILOT_IPC.setDocked, docked),

  updateSettings: (patch: SettingsPatch): Promise<CopilotSettings> =>
    ipcRenderer.invoke(COPILOT_IPC.updateSettings, patch),

  setApiKey: (key: string | null): Promise<ActionResult> => ipcRenderer.invoke(COPILOT_IPC.setApiKey, key),

  pickAttachments: (): Promise<PickedAttachment[]> => ipcRenderer.invoke(COPILOT_IPC.pickAttachments),

  openExternal: (url: string): void => {
    ipcRenderer.send(COPILOT_IPC.openExternal, url);
  },

  minimizeWindow: (): void => {
    ipcRenderer.send(COPILOT_IPC.minimizeWindow);
  },

  closeWindow: (): void => {
    ipcRenderer.send(COPILOT_IPC.closeWindow);
  },
};

contextBridge.exposeInMainWorld('copilot', bridge);
