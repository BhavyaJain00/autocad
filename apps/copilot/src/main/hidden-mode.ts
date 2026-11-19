/**
 * Hidden mode (CADCOPILOT_HIDDEN=1): the app runs normally but never puts anything on the user's
 * screen. Windows are created with `show: false`, keep painting (no background throttling, so
 * requestAnimationFrame and `webContents.capturePage()` work) and have no taskbar button; every
 * call that would show, raise, focus, maximise or minimise a window is a no-op; native dialogs
 * answer like a cancel without appearing; links are never opened in the browser.
 *
 * Used by the end-to-end tests and by screenshot scripts (docs/testing/hidden-windows.md).
 * Written against minimal interfaces so it is unit-testable without Electron.
 */

/** The environment variable that turns hidden mode on ('1'). */
export const HIDDEN_MODE_VARIABLE = 'CADCOPILOT_HIDDEN';

/** True when `env[variable]` is '1'. */
export function isHiddenMode(env: Readonly<Record<string, string | undefined>> = process.env, variable = HIDDEN_MODE_VARIABLE): boolean {
  return env[variable] === '1';
}

/** BrowserWindow methods that would show, raise or focus a window (or change its show state). */
export const BLOCKED_WINDOW_METHODS = [
  'show',
  'showInactive',
  'focus',
  'moveTop',
  'maximize',
  'minimize',
  'restore',
  'setFullScreen',
  'setSimpleFullScreen',
  'setKiosk',
  'flashFrame',
] as const;

/** BrowserWindow options added in hidden mode (merged over the regular ones). */
export function hiddenWindowOptions(hidden: boolean): { show: false; skipTaskbar?: true; paintWhenInitiallyHidden?: true } {
  return hidden ? { show: false, skipTaskbar: true, paintWhenInitiallyHidden: true } : { show: false };
}

/** Web preferences added in hidden mode: a never-shown page keeps rendering at full rate. */
export function hiddenWebPreferences(hidden: boolean): { backgroundThrottling?: false } {
  return hidden ? { backgroundThrottling: false } : {};
}

interface MessageBoxOptionsLike {
  readonly message?: string;
  readonly buttons?: readonly string[];
  readonly cancelId?: number;
}

/**
 * The button a message box answers with when nobody can see it: its `cancelId`, else a button
 * labelled Cancel or No (Electron's own default), else the first button.
 */
export function silentMessageBoxResponse(options: MessageBoxOptionsLike | undefined): number {
  if (options?.cancelId !== undefined) return options.cancelId;
  const buttons = options?.buttons ?? [];
  const cancel = buttons.findIndex((label) => /^(cancel|no)$/i.test(label.replace(/&/g, '').trim()));
  return cancel >= 0 ? cancel : 0;
}

/** Minimal shapes of the Electron modules the guards patch. */
export interface HiddenModeTargets {
  /** `BrowserWindow.prototype` (methods are replaced on it, so every window is covered). */
  readonly windowPrototype: object;
  readonly dialog: object;
  readonly shell?: object;
  readonly app?: object;
  readonly log: (message: string) => void;
}

function lastOptions(args: readonly unknown[]): MessageBoxOptionsLike | undefined {
  const last = args[args.length - 1];
  return typeof last === 'object' && last !== null ? (last as MessageBoxOptionsLike) : undefined;
}

function replace(target: object, name: string, implementation: (...args: unknown[]) => unknown): void {
  if (typeof (target as Record<string, unknown>)[name] !== 'function') return;
  Object.defineProperty(target, name, { value: implementation, writable: true, configurable: true });
}

/**
 * Patches the given Electron objects so nothing can appear on screen. Tests may still replace
 * the dialog functions afterwards (their stubs simply take over).
 */
export function installHiddenModeGuards(targets: HiddenModeTargets): void {
  const { log } = targets;
  for (const name of BLOCKED_WINDOW_METHODS) {
    replace(targets.windowPrototype, name, () => undefined);
  }
  if (targets.app !== undefined) replace(targets.app, 'focus', () => undefined);

  const { dialog } = targets;
  const describe = (args: readonly unknown[]): string => {
    const options = lastOptions(args);
    return options?.message !== undefined ? `"${options.message}"` : 'a dialog';
  };
  replace(dialog, 'showMessageBox', async (...args: unknown[]) => {
    const response = silentMessageBoxResponse(lastOptions(args));
    log(`Hidden mode: answered ${describe(args)} with button ${response} without showing it.`);
    return { response, checkboxChecked: false };
  });
  replace(dialog, 'showMessageBoxSync', (...args: unknown[]) => {
    const response = silentMessageBoxResponse(lastOptions(args));
    log(`Hidden mode: answered ${describe(args)} with button ${response} without showing it.`);
    return response;
  });
  replace(dialog, 'showOpenDialog', async () => {
    log('Hidden mode: cancelled an open dialog without showing it.');
    return { canceled: true, filePaths: [] };
  });
  replace(dialog, 'showOpenDialogSync', () => {
    log('Hidden mode: cancelled an open dialog without showing it.');
    return undefined;
  });
  replace(dialog, 'showSaveDialog', async () => {
    log('Hidden mode: cancelled a save dialog without showing it.');
    return { canceled: true, filePath: '' };
  });
  replace(dialog, 'showSaveDialogSync', () => {
    log('Hidden mode: cancelled a save dialog without showing it.');
    return '';
  });
  replace(dialog, 'showErrorBox', (title: unknown, content: unknown) => {
    log(`Hidden mode: error box not shown: ${String(title)}: ${String(content)}`);
  });
  replace(dialog, 'showCertificateTrustDialog', async () => undefined);

  if (targets.shell !== undefined) {
    replace(targets.shell, 'openExternal', async (url: unknown) => {
      log(`Hidden mode: not opening ${String(url)}.`);
    });
    replace(targets.shell, 'openPath', async (target: unknown) => {
      log(`Hidden mode: not opening ${String(target)}.`);
      return '';
    });
    replace(targets.shell, 'showItemInFolder', (target: unknown) => {
      log(`Hidden mode: not revealing ${String(target)}.`);
    });
  }
}
