/**
 * Creation of the single CAD Copilot window: a narrow panel with a hidden title bar (the renderer
 * draws the draggable header; the native minimise / close buttons sit on top of it), secure web
 * preferences, development shortcuts and loading of the renderer.
 */
import { BrowserWindow, screen, type Input } from 'electron';
import { PANEL_WIDTH_MIN } from '../shared/bridge';
import {
  APP_NAME,
  TITLE_BAR_HEIGHT,
  TITLE_BAR_SYMBOL_COLOR,
  WINDOW_BACKGROUND_COLOR,
  WINDOW_DEFAULT_HEIGHT,
  WINDOW_MIN_HEIGHT,
} from './constants';
import { hiddenWebPreferences, hiddenWindowOptions } from './hidden-mode';

/** Gap (DIP) between the window and the right edge of the work area when it first opens. */
const INITIAL_EDGE_MARGIN = 24;

export interface CopilotWindowOptions {
  /** Absolute path of the bundled preload script. */
  readonly preloadPath: string;
  /** Absolute path of the built renderer's index.html (production). */
  readonly rendererIndexPath: string;
  /** Vite dev server URL in development, null otherwise. */
  readonly devServerUrl: string | null;
  /** `app.isPackaged`: disables DevTools and blocks reload shortcuts. */
  readonly isPackaged: boolean;
  /** Initial width (DIP): the panel width from the settings. */
  readonly panelWidth: number;
  readonly alwaysOnTop: boolean;
  /** Hidden mode (CADCOPILOT_HIDDEN=1): never shown, no taskbar button, keeps painting. */
  readonly hidden?: boolean;
}

/**
 * Where the window first opens: the panel width by the default height (shrunk to the primary
 * display's work area), near the right edge and vertically centred. Without display information
 * the operating system chooses the position.
 */
function initialBounds(panelWidth: number): { width: number; height: number; x?: number; y?: number } {
  try {
    const { workArea } = screen.getPrimaryDisplay();
    const width = Math.max(PANEL_WIDTH_MIN, Math.min(panelWidth, workArea.width));
    const height = Math.max(WINDOW_MIN_HEIGHT, Math.min(WINDOW_DEFAULT_HEIGHT, workArea.height));
    const margin = workArea.width - width >= INITIAL_EDGE_MARGIN * 2 ? INITIAL_EDGE_MARGIN : 0;
    return {
      width,
      height,
      x: Math.round(workArea.x + workArea.width - width - margin),
      y: Math.round(workArea.y + Math.max(0, (workArea.height - height) / 2)),
    };
  } catch {
    return { width: panelWidth, height: WINDOW_DEFAULT_HEIGHT };
  }
}

function isDevToolsShortcut(input: Input, primary: boolean): boolean {
  const key = input.key.toLowerCase();
  return key === 'f12' || (primary && input.shift && !input.alt && key === 'i');
}

function isReloadShortcut(input: Input, primary: boolean): boolean {
  const key = input.key.toLowerCase();
  return key === 'f5' || (primary && !input.alt && key === 'r');
}

/**
 * Development: F12 / Ctrl+Shift+I toggle DevTools. Packaged: F5 / Ctrl+R / Ctrl+Shift+R are
 * swallowed so the panel is never reloaded by accident.
 */
function installKeyboardShortcuts(win: BrowserWindow, isPackaged: boolean): void {
  win.webContents.on('before-input-event', (event, input) => {
    if (input.type !== 'keyDown') return;
    const primary = process.platform === 'darwin' ? input.meta : input.control;
    if (!isPackaged) {
      if (isDevToolsShortcut(input, primary)) {
        event.preventDefault();
        win.webContents.toggleDevTools();
      }
      return;
    }
    if (isReloadShortcut(input, primary)) event.preventDefault();
  });
}

async function loadRenderer(win: BrowserWindow, options: CopilotWindowOptions): Promise<void> {
  try {
    if (options.devServerUrl !== null) {
      await win.loadURL(options.devServerUrl);
    } else {
      await win.loadFile(options.rendererIndexPath);
    }
  } catch (error) {
    // did-fail-load reports details and shows the window.
    console.error('[window] Failed to load the renderer:', error);
  }
}

/**
 * Creates the copilot window (hidden until its first paint; never shown in hidden mode) and
 * starts loading the renderer.
 */
export function createCopilotWindow(options: CopilotWindowOptions): BrowserWindow {
  const hidden = options.hidden === true;
  const win = new BrowserWindow({
    ...initialBounds(options.panelWidth),
    minWidth: PANEL_WIDTH_MIN,
    minHeight: WINDOW_MIN_HEIGHT,
    backgroundColor: WINDOW_BACKGROUND_COLOR,
    ...hiddenWindowOptions(hidden),
    title: APP_NAME,
    titleBarStyle: 'hidden',
    titleBarOverlay: {
      color: WINDOW_BACKGROUND_COLOR,
      symbolColor: TITLE_BAR_SYMBOL_COLOR,
      height: TITLE_BAR_HEIGHT,
    },
    autoHideMenuBar: true,
    // A side panel: it is resized and docked, never maximised or made full screen.
    maximizable: false,
    fullscreenable: false,
    alwaysOnTop: options.alwaysOnTop,
    webPreferences: {
      ...hiddenWebPreferences(hidden),
      preload: options.preloadPath,
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      nodeIntegrationInSubFrames: false,
      nodeIntegrationInWorker: false,
      webSecurity: true,
      allowRunningInsecureContent: false,
      webviewTag: false,
      navigateOnDragDrop: false,
      spellcheck: false,
      devTools: !options.isPackaged,
    },
  });
  win.setMenuBarVisibility(false);

  win.once('ready-to-show', () => {
    if (!hidden && !win.isDestroyed()) win.show();
  });
  // The title belongs to the main process, never to the page's <title>.
  win.on('page-title-updated', (event) => {
    event.preventDefault();
  });

  const { webContents } = win;
  webContents.on('did-fail-load', (_event, errorCode, errorDescription, validatedUrl, isMainFrame) => {
    // -3 (ERR_ABORTED) is a navigation superseded by another one, not a failure.
    if (!isMainFrame || errorCode === -3) return;
    console.error(`[window] Renderer failed to load ${validatedUrl}: ${errorDescription} (${errorCode})`);
    // Never leave an invisible window behind (unless it is meant to be invisible).
    if (!hidden && !win.isDestroyed() && !win.isVisible()) win.show();
  });
  webContents.on('preload-error', (_event, preloadPath, error) => {
    console.error(`[window] Preload script ${preloadPath} failed:`, error);
  });

  installKeyboardShortcuts(win, options.isPackaged);
  void loadRenderer(win, options);
  return win;
}
