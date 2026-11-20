"use strict";

// src/preload/preload.ts
var import_electron = require("electron");

// src/shared/bridge.ts
var ATTACHMENT_MAX_BYTES = 10 * 1024 * 1024;
var COPILOT_IPC = {
  getSnapshot: "copilot:get-snapshot",
  timeline: "copilot:timeline",
  connection: "copilot:connection",
  connectors: "copilot:connectors",
  settings: "copilot:settings",
  busy: "copilot:busy",
  sendMessage: "copilot:send-message",
  answerQuestion: "copilot:answer-question",
  resolveApproval: "copilot:resolve-approval",
  stop: "copilot:stop",
  resetSession: "copilot:reset-session",
  refreshConnectors: "copilot:refresh-connectors",
  connect: "copilot:connect",
  disconnect: "copilot:disconnect",
  setDocked: "copilot:set-docked",
  updateSettings: "copilot:update-settings",
  setApiKey: "copilot:set-api-key",
  pickAttachments: "copilot:pick-attachments",
  openExternal: "copilot:open-external",
  minimizeWindow: "copilot:window-minimize",
  closeWindow: "copilot:window-close"
};

// src/preload/preload.ts
function subscribe(channel, listener) {
  if (typeof listener !== "function") throw new TypeError("The listener must be a function.");
  const handler = (_event, payload) => {
    try {
      listener(payload);
    } catch (error) {
      console.error(`[copilot] A listener of ${channel} failed:`, error);
    }
  };
  import_electron.ipcRenderer.on(channel, handler);
  let subscribed = true;
  return () => {
    if (!subscribed) return;
    subscribed = false;
    import_electron.ipcRenderer.removeListener(channel, handler);
  };
}
var bridge = {
  getSnapshot: () => import_electron.ipcRenderer.invoke(COPILOT_IPC.getSnapshot),
  onTimeline: (listener) => subscribe(COPILOT_IPC.timeline, listener),
  onConnection: (listener) => subscribe(COPILOT_IPC.connection, listener),
  onConnectors: (listener) => subscribe(COPILOT_IPC.connectors, listener),
  onSettings: (listener) => subscribe(COPILOT_IPC.settings, listener),
  onBusy: (listener) => subscribe(COPILOT_IPC.busy, listener),
  sendMessage: (text, attachments) => import_electron.ipcRenderer.invoke(COPILOT_IPC.sendMessage, text, attachments),
  answerQuestion: (requestId, answers) => {
    import_electron.ipcRenderer.send(COPILOT_IPC.answerQuestion, requestId, answers);
  },
  resolveApproval: (requestId, decision) => {
    import_electron.ipcRenderer.send(COPILOT_IPC.resolveApproval, requestId, decision);
  },
  stop: () => {
    import_electron.ipcRenderer.send(COPILOT_IPC.stop);
  },
  resetSession: () => {
    import_electron.ipcRenderer.send(COPILOT_IPC.resetSession);
  },
  refreshConnectors: () => import_electron.ipcRenderer.invoke(COPILOT_IPC.refreshConnectors),
  connect: (connectorId) => import_electron.ipcRenderer.invoke(COPILOT_IPC.connect, connectorId),
  disconnect: () => {
    import_electron.ipcRenderer.send(COPILOT_IPC.disconnect);
  },
  setDocked: (docked) => import_electron.ipcRenderer.invoke(COPILOT_IPC.setDocked, docked),
  updateSettings: (patch) => import_electron.ipcRenderer.invoke(COPILOT_IPC.updateSettings, patch),
  setApiKey: (key) => import_electron.ipcRenderer.invoke(COPILOT_IPC.setApiKey, key),
  pickAttachments: () => import_electron.ipcRenderer.invoke(COPILOT_IPC.pickAttachments),
  openExternal: (url) => {
    import_electron.ipcRenderer.send(COPILOT_IPC.openExternal, url);
  },
  minimizeWindow: () => {
    import_electron.ipcRenderer.send(COPILOT_IPC.minimizeWindow);
  },
  closeWindow: () => {
    import_electron.ipcRenderer.send(COPILOT_IPC.closeWindow);
  }
};
import_electron.contextBridge.exposeInMainWorld("copilot", bridge);
//# sourceMappingURL=preload.cjs.map
