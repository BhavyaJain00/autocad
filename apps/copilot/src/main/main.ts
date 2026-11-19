/**
 * CAD Copilot main process entry (bundled by scripts/build-electron.mjs to dist/electron/main.cjs).
 *
 * One narrow window whose renderer is a pure view of `window.copilot`. Everything else lives
 * here: the CopilotSession (Claude tool-use loop), the connection to a CAD application over its
 * named pipe, docking beside the CAD window, settings, the encrypted API key and attachments.
 */
import path from 'node:path';
import { connectToConnector, listAdverts } from '@cadstudio/cad-protocol/node';
import { CopilotSession, createScriptedTransport, verifyApiKey, type ModelTransport } from '@cadstudio/copilot-agent';
import { app, BrowserWindow, dialog, Menu, safeStorage, screen, session, shell } from 'electron';
import {
  COPILOT_IPC,
  type ActionResult,
  type CopilotSettings,
  type CopilotSnapshot,
  type PickedAttachment,
  type SettingsPatch,
} from '../shared/bridge';
import { ATTACHMENT_DIALOG_FILTERS, readPickedAttachments } from './attachments';
import { ConnectorManager } from './connectors';
import {
  APP_NAME,
  APP_USER_MODEL_ID,
  ENV_API_KEY,
  ENV_CONNECT,
  ENV_USER_DATA,
  WINDOW_RESIZE_DEBOUNCE_MS,
} from './constants';
import { CredentialStore } from './credentials';
import { installHiddenModeGuards, isHiddenMode } from './hidden-mode';
import { DockingController, WINDOWS_INVISIBLE_FRAME, type DockScreen } from './docking';
import { createIpcHandlers } from './ipc-handlers';
import { registerIpcHandlers } from './ipc';
import { errorMessage } from './json-file';
import { installSessionSecurity, installWebContentsSecurity } from './security';
import { SessionHost } from './session-host';
import { clampPanelWidth, SettingsStore } from './settings';
import { loadTestModel } from './test-model';
import { parseDevServerUrl } from './url-policy';
import { checkConnectorId } from './validation';
import { createCopilotWindow } from './window';

/** With ELECTRON_RUN_AS_NODE set, `require('electron')` is a path string and nothing works. */
function exitUnlessRunningInElectron(): void {
  if (typeof app === 'undefined' || typeof app.whenReady !== 'function') {
    console.error(
      'CAD Copilot must be started by Electron (`npm run start:copilot` or `npm run dev:copilot`). ' +
        'If ELECTRON_RUN_AS_NODE is set in this environment, unset it.',
    );
    process.exit(1);
  }
}

exitUnlessRunningInElectron();
process.setSourceMapsEnabled(true);

/**
 * Hidden mode (CADCOPILOT_HIDDEN=1, set by the end-to-end helpers and inherited from a hidden CAD
 * Studio): nothing ever appears on screen, docking included (see hidden-mode.ts).
 */
const hiddenMode = isHiddenMode(process.env);
if (hiddenMode) {
  installHiddenModeGuards({
    windowPrototype: BrowserWindow.prototype,
    dialog,
    shell,
    app,
    log: (message) => console.warn(`[hidden] ${message}`),
  });
}

/** Longest the shutdown work (restoring the CAD window, saving settings) may delay quitting. */
const SHUTDOWN_DEADLINE_MS = 3_000;

// The name decides the default userData folder, so it is set before any path is read; the
// override (tests, portable setups) must also be in place before the single-instance lock.
app.setName(APP_NAME);
const userDataOverride = process.env[ENV_USER_DATA]?.trim();
if (userDataOverride !== undefined && userDataOverride.length > 0) {
  app.setPath('userData', path.resolve(userDataOverride));
}

/** VITE_DEV_SERVER_URL is honoured only in development, never by a packaged build. */
const devServerUrl = app.isPackaged ? null : parseDevServerUrl(process.env.VITE_DEV_SERVER_URL);
if (!app.isPackaged && process.env.VITE_DEV_SERVER_URL && devServerUrl === null) {
  console.warn(`[app] Ignoring invalid VITE_DEV_SERVER_URL: ${process.env.VITE_DEV_SERVER_URL}`);
}

/** The connector a CAD application asked this copilot to attach to (its advert id), if any. */
const requestedConnectorId = checkConnectorId(process.env[ENV_CONNECT]);

const log = (scope: string) => (message: string): void => {
  console.warn(`[${scope}] ${message}`);
};

let mainWindow: BrowserWindow | null = null;

function liveWindow(): BrowserWindow | null {
  return mainWindow !== null && !mainWindow.isDestroyed() ? mainWindow : null;
}

/** Sends a push message to the renderer when one is attached; never throws. */
function pushToRenderer(channel: string, payload: unknown): void {
  const win = liveWindow();
  if (win === null) return;
  const { webContents } = win;
  if (webContents.isDestroyed() || webContents.isCrashed()) return;
  try {
    webContents.send(channel, payload);
  } catch (error) {
    console.error(`[ipc] Sending ${channel} failed:`, error);
  }
}

function showAndFocusWindow(): void {
  const win = liveWindow();
  if (win === null || hiddenMode) return;
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}

/** Builds and runs the application. Only the instance holding the single-instance lock gets here. */
function start(): void {
  if (process.platform === 'win32') app.setAppUserModelId(APP_USER_MODEL_ID);
  // The renderer draws its own header; no native menu (and none of its accelerators).
  Menu.setApplicationMenu(null);

  let testModel: ModelTransport | null;
  try {
    testModel = loadTestModel({ env: process.env, createTransport: createScriptedTransport });
  } catch (error) {
    console.error(`[app] ${errorMessage(error)}`);
    app.exit(1);
    return;
  }
  if (testModel !== null) console.warn('[app] Using the scripted TEST MODEL instead of Claude.');

  const userData = app.getPath('userData');
  const settings = new SettingsStore(path.join(userData, 'settings.json'), { log: log('settings') });
  settings.load();

  const credentials = new CredentialStore({
    filePath: path.join(userData, 'credentials.json'),
    protector: safeStorage,
    environmentKey: () => process.env[ENV_API_KEY],
    log: log('credentials'),
  });

  const currentSettings = (): CopilotSettings => ({
    ...settings.get(),
    hasApiKey: credentials.hasApiKey(),
    testModel: testModel !== null,
  });
  const pushSettings = (): void => pushToRenderer(COPILOT_IPC.settings, currentSettings());

  const sessionHost = new SessionHost({
    createSession: () => {
      const { model, effort, approvalMode } = settings.get();
      return new CopilotSession({
        apiKey: credentials.apiKey(),
        model,
        effort,
        approvalMode,
        executor: null,
        ...(testModel !== null ? { transport: testModel } : {}),
      });
    },
    onTimeline: (entry) => pushToRenderer(COPILOT_IPC.timeline, entry),
    onBusy: (busy) => pushToRenderer(COPILOT_IPC.busy, busy),
    log: log('session'),
  });

  /** Electron's `screen` may only be used once the app is ready, so it is reached lazily. */
  const dockScreen: DockScreen = {
    getDisplayMatching: (rect) => screen.getDisplayMatching(rect),
    screenToDipRect: (_window, rect) =>
      typeof screen.screenToDipRect === 'function' ? screen.screenToDipRect(null, rect) : rect,
    dipToScreenRect: (_window, rect) =>
      typeof screen.dipToScreenRect === 'function' ? screen.dipToScreenRect(null, rect) : rect,
  };

  const docking: DockingController = new DockingController({
    screen: dockScreen,
    getWindow: liveWindow,
    getConnection: () => connectors.connection,
    getPanelWidth: () => settings.get().panelWidth,
    setPanelWidth: (width) => {
      if (settings.update({ panelWidth: width }).length > 0) pushSettings();
    },
    onDockedChanged: (docked) => connectors.setDocked(docked),
    // CAD windows (CAD Studio, AutoCAD) are standard framed windows with invisible borders.
    cadFrameInset: process.platform === 'win32' ? WINDOWS_INVISIBLE_FRAME : 0,
    log: log('docking'),
  });

  const connectors: ConnectorManager = new ConnectorManager({
    listAdverts,
    connect: (advert) => connectToConnector(advert, { clientName: APP_NAME, clientVersion: app.getVersion() }),
    autoConnectEnabled: () => settings.get().autoConnect,
    preferredConnectorId: requestedConnectorId,
    onConnectors: (list) => pushToRenderer(COPILOT_IPC.connectors, list),
    onConnection: (state) => pushToRenderer(COPILOT_IPC.connection, state),
    onExecutor: (executor) => sessionHost.setExecutor(executor),
    afterConnected: async () => {
      if (!settings.get().autoDock) return;
      const docked = await docking.dock();
      if (!docked.ok) console.warn(`[docking] Docking after connecting failed: ${docked.message}`);
    },
    beforeDisconnect: async () => {
      await docking.undock();
    },
    onConnectionEnded: () => docking.forget(),
    log: log('connectors'),
  });

  /** Applies a new panel width to the window: re-fits the docked pair, or resizes the free window. */
  function applyPanelWidth(width: number): void {
    if (docking.docked) {
      void docking.dock().then((result) => {
        if (!result.ok) console.warn(`[docking] Re-fitting failed: ${result.message}`);
      });
      return;
    }
    const win = liveWindow();
    if (win === null || win.isMinimized()) return;
    const bounds = win.getBounds();
    if (bounds.width === width) return;
    // Keep the right edge where it is: the panel grows and shrinks towards the CAD.
    win.setBounds({ x: bounds.x + bounds.width - width, y: bounds.y, width, height: bounds.height });
  }

  function updateSettings(patch: SettingsPatch): CopilotSettings {
    const changed = settings.update(patch);
    if (changed.length === 0) return currentSettings();
    const now = settings.get();
    sessionHost.applySettings({
      ...(changed.includes('model') ? { model: now.model } : {}),
      ...(changed.includes('effort') ? { effort: now.effort } : {}),
      ...(changed.includes('approvalMode') ? { approvalMode: now.approvalMode } : {}),
    });
    if (changed.includes('alwaysOnTop')) liveWindow()?.setAlwaysOnTop(now.alwaysOnTop);
    if (changed.includes('panelWidth')) applyPanelWidth(now.panelWidth);
    if (changed.includes('autoConnect') && now.autoConnect) {
      void connectors.resumeAutoConnect().catch((error: unknown) => {
        console.warn(`[connectors] Checking for CAD applications failed: ${errorMessage(error)}`);
      });
    }
    pushSettings();
    return currentSettings();
  }

  async function setApiKey(key: string | null): Promise<ActionResult> {
    if (key === null) {
      const removed = await credentials.clear();
      if (!removed.ok) return removed;
      sessionHost.setApiKey(credentials.apiKey());
      pushSettings();
      return { ok: true };
    }
    // Refuse before going to the network when the key could not be stored anyway.
    const problem = credentials.storageProblem();
    if (problem !== null) return { ok: false, message: problem };
    let verdict: ActionResult;
    try {
      verdict = await verifyApiKey(key);
    } catch (error) {
      verdict = { ok: false, message: `The API key could not be checked: ${errorMessage(error)}` };
    }
    if (!verdict.ok) return verdict;
    const stored = await credentials.store(key);
    if (!stored.ok) return stored;
    sessionHost.setApiKey(key);
    pushSettings();
    return { ok: true };
  }

  async function pickAttachments(): Promise<PickedAttachment[]> {
    const win = liveWindow();
    if (win === null) return [];
    const result = await dialog.showOpenDialog(win, {
      title: 'Attach files',
      buttonLabel: 'Attach',
      properties: ['openFile', 'multiSelections'],
      filters: ATTACHMENT_DIALOG_FILTERS,
    });
    if (result.canceled || result.filePaths.length === 0) return [];
    return readPickedAttachments(result.filePaths, { log: log('attachments') });
  }

  function snapshot(): CopilotSnapshot {
    return {
      appVersion: app.getVersion(),
      settings: currentSettings(),
      connection: connectors.state,
      connectors: connectors.connectors,
      busy: sessionHost.busy,
      timeline: sessionHost.timeline(),
    };
  }

  const handlers = createIpcHandlers(
    {
      getSnapshot: snapshot,
      sendMessage: (text, attachments) => sessionHost.sendMessage(text, attachments),
      answerQuestion: (requestId, answers) => sessionHost.answerQuestion(requestId, answers),
      resolveApproval: (requestId, decision) => sessionHost.resolveApproval(requestId, decision),
      stop: () => sessionHost.stop(),
      resetSession: () => sessionHost.reset(),
      refreshConnectors: () => connectors.refresh(),
      connect: (connectorId) => connectors.connect(connectorId),
      disconnect: () => {
        void connectors.disconnect().catch((error: unknown) => {
          console.warn(`[connectors] Disconnecting failed: ${errorMessage(error)}`);
        });
      },
      setDocked: (docked) => (docked ? docking.dock() : docking.undock()),
      currentSettings,
      updateSettings,
      setApiKey,
      pickAttachments,
      openExternal: (url) => {
        void shell.openExternal(url).catch((error: unknown) => {
          console.warn(`[app] Opening ${url} failed: ${errorMessage(error)}`);
        });
      },
      minimizeWindow: () => liveWindow()?.minimize(),
      closeWindow: () => liveWindow()?.close(),
    },
    log('ipc'),
  );

  /** The user stopped resizing the window: the new width becomes the panel width. */
  function windowResized(): void {
    const win = liveWindow();
    if (win === null || win.isMinimized() || win.isMaximized() || win.isFullScreen()) return;
    if (docking.docked) {
      void docking.windowResized();
      return;
    }
    const width = clampPanelWidth(win.getBounds().width);
    if (settings.update({ panelWidth: width }).length > 0) pushSettings();
  }

  function offerRecoveryAfterRendererCrash(win: BrowserWindow, reason: string): void {
    dialog
      .showMessageBox(win, {
        type: 'error',
        title: APP_NAME,
        buttons: ['Reload', 'Close'],
        defaultId: 0,
        cancelId: 1,
        noLink: true,
        message: 'The CAD Copilot panel stopped unexpectedly.',
        detail: `Reason: ${reason}. The conversation is kept; reload to continue.`,
      })
      .then(({ response }) => {
        if (win.isDestroyed()) return;
        if (response === 0) win.webContents.reload();
        else win.close();
      })
      .catch((error: unknown) => console.error('[app] Crash recovery prompt failed:', error));
  }

  function openWindow(): void {
    const { panelWidth, alwaysOnTop } = settings.get();
    const win = createCopilotWindow({
      preloadPath: path.join(__dirname, 'preload.cjs'),
      rendererIndexPath: path.join(__dirname, '../renderer/index.html'),
      devServerUrl,
      isPackaged: app.isPackaged,
      panelWidth,
      alwaysOnTop,
      hidden: hiddenMode,
    });
    mainWindow = win;

    let resizeTimer: ReturnType<typeof setTimeout> | undefined;
    win.on('resize', () => {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(windowResized, WINDOW_RESIZE_DEBOUNCE_MS);
    });
    win.webContents.on('render-process-gone', (_event, details) => {
      if (details.reason === 'clean-exit' || win.isDestroyed()) return;
      console.error(`[app] Renderer process gone: ${details.reason} (exit code ${details.exitCode})`);
      offerRecoveryAfterRendererCrash(win, details.reason);
    });
    win.on('closed', () => {
      clearTimeout(resizeTimer);
      if (mainWindow === win) mainWindow = null;
    });
  }

  /** Stops the turn, restores the CAD window, closes the connection and saves the settings. */
  async function shutdown(): Promise<void> {
    sessionHost.dispose();
    await connectors.stop();
    await settings.flush();
  }

  installWebContentsSecurity(devServerUrl);
  registerIpcHandlers({ getWindow: liveWindow, devServerUrl, handlers });

  // A second launch focuses this window; when a CAD application started it, attach to that CAD.
  app.on('second-instance', (_event, _argv, _workingDirectory, additionalData) => {
    showAndFocusWindow();
    const connectorId = checkConnectorId((additionalData as { connect?: unknown } | null)?.connect);
    if (connectorId === null) return;
    void connectors.requestConnect(connectorId).catch((error: unknown) => {
      console.warn(`[connectors] Connecting to ${connectorId} failed: ${errorMessage(error)}`);
    });
  });

  app.on('window-all-closed', () => {
    app.quit();
  });

  // Quitting waits (briefly) for the CAD window to be restored and the connection to close; the
  // second quit then goes through.
  let shutdownStarted = false;
  app.on('will-quit', (event) => {
    if (shutdownStarted) return;
    shutdownStarted = true;
    event.preventDefault();
    const deadline = new Promise<void>((resolve) => setTimeout(resolve, SHUTDOWN_DEADLINE_MS));
    void Promise.race([shutdown(), deadline])
      .catch((error: unknown) => console.error('[app] Shutdown failed:', error))
      .finally(() => app.quit());
  });

  app
    .whenReady()
    .then(() => {
      installSessionSecurity(session.defaultSession);
      // The operating system's credential encryption is available only now.
      credentials.load();
      sessionHost.start();
      openWindow();
      connectors.start();
    })
    .catch((error: unknown) => {
      console.error('[app] Startup failed:', error);
      app.exit(1);
    });
}

// One copilot per user profile: a second launch hands its connect request to the first and exits.
if (app.requestSingleInstanceLock({ connect: requestedConnectorId })) {
  start();
} else {
  app.quit();
}
