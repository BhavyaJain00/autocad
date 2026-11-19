/**
 * Helpers for the joint end-to-end tests of CAD Copilot with the REAL CAD Studio app
 * (apps/desktop, built into its dist/ folder): launching CAD Studio with an isolated connector
 * folder, reading its drawing through the e2e hook `window.__cadstudioTest.editor`, window
 * bounds, and killing process trees.
 */
import { spawnSync, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { _electron as electron, type ElectronApplication, type Page } from '@playwright/test';
import { appDir } from './helpers';

/** apps/desktop: Electron loads its package.json "main" (dist/electron/main.cjs) from here. */
export const studioDir = path.resolve(appDir, '..', 'desktop');

/** Variables of the test environment that must never leak into CAD Studio. */
const BLOCKED_VARIABLES: ReadonlySet<string> = new Set([
  'ELECTRON_RUN_AS_NODE',
  'VITE_DEV_SERVER_URL',
  'CADSTUDIO_NO_COPILOT',
  'ANTHROPIC_API_KEY',
  'CADCOPILOT_USER_DATA',
  'CADCOPILOT_HOME',
  'CADCOPILOT_CONNECT',
  'CADCOPILOT_SCRIPT',
  'CADCOPILOT_E2E',
]);

export interface Point {
  readonly x: number;
  readonly y: number;
}

export interface Bounds {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

/** The parts of CAD Studio's DocumentData these tests read. */
export interface StudioEntity {
  readonly id: string;
  readonly type: string;
  readonly layerId: string;
  readonly origin?: Point;
  readonly width?: number;
  readonly height?: number;
  readonly center?: Point;
  readonly radius?: number;
  readonly start?: Point;
  readonly end?: Point;
}

export interface StudioDocument {
  readonly name: string;
  readonly layers: readonly { readonly id: string; readonly name: string }[];
  readonly entities: readonly StudioEntity[];
}

/** An entry of the document's undo stack (operation history). */
export interface StudioOperation {
  readonly type: string;
  readonly label: string;
  readonly source: string;
  readonly instruction?: string;
  readonly targetIds: readonly string[];
}

/** The page's `window` as these tests use it (the e2e tsconfig has no DOM library). */
type StudioGlobal = typeof globalThis & {
  __cadstudioTest?: {
    editor: {
      getDocumentData(): StudioDocument;
      readonly document: { readonly undoStack: readonly StudioOperation[] };
    };
  };
};

export interface LaunchedStudio {
  readonly app: ElectronApplication;
  readonly page: Page;
  /** Pid of CAD Studio's Electron main process. */
  readonly pid: number;
  /** Advert id of its copilot connector ('cadstudio-<pid>'). */
  readonly advertId: string;
}

export interface StudioLaunchOptions {
  /** CADCOPILOT_HOME: the folder CAD Studio's connector advertises itself in. */
  readonly homeDir: string;
  /** Extra variables (they are also inherited by a copilot CAD Studio starts). */
  readonly env?: Readonly<Record<string, string>>;
}

const childProcesses = new WeakMap<ElectronApplication, ChildProcess>();

function studioEnvironment(options: StudioLaunchOptions): Record<string, string> {
  const env: Record<string, string> = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (value !== undefined && !BLOCKED_VARIABLES.has(key.toUpperCase())) env[key] = value;
  }
  return { ...env, CADSTUDIO_E2E: '1', CADCOPILOT_HOME: options.homeDir, ...options.env };
}

/** Launches the built CAD Studio and waits until its editor and e2e hook are ready. */
export async function launchStudio(options: StudioLaunchOptions): Promise<LaunchedStudio> {
  const app = await electron.launch({ args: [studioDir], cwd: studioDir, env: studioEnvironment(options) });
  childProcesses.set(app, app.process());
  try {
    const page = await app.firstWindow();
    await page.waitForLoadState('domcontentloaded');
    await page.getByTestId('app-root').waitFor({ state: 'visible', timeout: 30_000 });
    await page.waitForFunction(() => (globalThis as StudioGlobal).__cadstudioTest !== undefined, undefined, { timeout: 30_000 });
    const pid = await app.evaluate(() => process.pid);
    return { app, page, pid, advertId: `cadstudio-${pid}` };
  } catch (error) {
    await closeStudio(app);
    throw error;
  }
}

function hasExited(app: ElectronApplication): boolean {
  const child = childProcesses.get(app);
  return child === undefined || child.exitCode !== null || child.signalCode !== null;
}

/** Resolves when CAD Studio's process has exited (immediately if it already has). */
export function waitForStudioExit(app: ElectronApplication): Promise<void> {
  const child = childProcesses.get(app);
  if (child === undefined || hasExited(app)) return Promise.resolve();
  return new Promise((resolve) => child.once('exit', () => resolve()));
}

/** Force-kills a process and every process it started (Windows: taskkill /T /F). */
export function killProcessTree(pid: number | undefined): void {
  if (pid === undefined || pid <= 0) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
    return;
  }
  try {
    process.kill(pid, 'SIGKILL');
  } catch {
    // Already gone.
  }
}

/** Ends CAD Studio at once (no unsaved-changes prompt), killing its process tree if needed. */
export async function closeStudio(app: ElectronApplication | undefined): Promise<void> {
  if (app === undefined || hasExited(app)) return;
  const exited = waitForStudioExit(app);
  await app
    .evaluate(({ app: electronApp }) => {
      setTimeout(() => electronApp.exit(0), 0);
    })
    .catch(() => undefined);
  const timedOut = await Promise.race([
    exited.then(() => false),
    new Promise<boolean>((resolve) => setTimeout(() => resolve(true), 10_000)),
  ]);
  if (timedOut && !hasExited(app)) {
    killProcessTree(childProcesses.get(app)?.pid);
    await exited;
  }
}

/** Kills CAD Studio the hard way (a crash): its main process tree and Playwright's child. */
export async function killStudio(studio: LaunchedStudio): Promise<void> {
  const exited = waitForStudioExit(studio.app);
  killProcessTree(studio.pid);
  killProcessTree(childProcesses.get(studio.app)?.pid);
  await exited;
}

/** CAD Studio's drawing as its editor holds it. */
export function studioDocument(page: Page): Promise<StudioDocument> {
  return page.evaluate(() => (globalThis as StudioGlobal).__cadstudioTest!.editor.getDocumentData());
}

/** CAD Studio's operation history (undo stack, oldest first). */
export function studioHistory(page: Page): Promise<StudioOperation[]> {
  return page.evaluate(() =>
    (globalThis as StudioGlobal).__cadstudioTest!.editor.document.undoStack.map((operation) => ({
      type: operation.type,
      label: operation.label,
      source: operation.source,
      ...(operation.instruction !== undefined ? { instruction: operation.instruction } : {}),
      targetIds: [...operation.targetIds],
    })),
  );
}

/** Outer bounds (DIP) of an app's first BrowserWindow. */
export function windowBounds(app: ElectronApplication): Promise<Bounds> {
  return app.evaluate(({ BrowserWindow }) => {
    const win = BrowserWindow.getAllWindows()[0];
    if (win === undefined) throw new Error('The app has no window');
    return win.getBounds();
  });
}

/** Work area (DIP) of the display a window is on. */
export function workAreaOf(app: ElectronApplication): Promise<Bounds> {
  return app.evaluate(({ BrowserWindow, screen }) => {
    const win = BrowserWindow.getAllWindows()[0];
    if (win === undefined) throw new Error('The app has no window');
    return screen.getDisplayMatching(win.getBounds()).workArea;
  });
}

/**
 * Saves a capture of the whole primary screen (Windows only; page screenshots do not show window
 * frames or what lies between two windows). Returns false when it could not be taken.
 */
export function captureScreen(file: string): boolean {
  if (process.platform !== 'win32') return false;
  const script = [
    'Add-Type -AssemblyName System.Windows.Forms, System.Drawing',
    '$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds',
    '$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height',
    '$g = [System.Drawing.Graphics]::FromImage($bmp)',
    '$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)',
    `$bmp.Save('${file.replace(/'/g, "''")}', [System.Drawing.Imaging.ImageFormat]::Png)`,
    '$g.Dispose(); $bmp.Dispose()',
  ].join('; ');
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true });
  return result.status === 0;
}

/** Electron processes whose parent is `parentPid` and that run the given app folder (Windows). */
export function childAppProcesses(parentPid: number, appFolderName: string): { pid: number; commandLine: string }[] {
  if (process.platform !== 'win32') return [];
  const command =
    `Get-CimInstance Win32_Process -Filter "ParentProcessId=${parentPid}" | ` +
    'Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress';
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command], {
    encoding: 'utf8',
    windowsHide: true,
  });
  const text = (result.stdout ?? '').trim();
  if (text === '') return [];
  const parsed = JSON.parse(text) as { ProcessId: number; CommandLine: string | null } | { ProcessId: number; CommandLine: string | null }[];
  const list = Array.isArray(parsed) ? parsed : [parsed];
  return list
    .filter((entry) => {
      const line = (entry.CommandLine ?? '').toLowerCase();
      return line.includes(appFolderName.toLowerCase()) && !line.includes('--type=');
    })
    .map((entry) => ({ pid: entry.ProcessId, commandLine: entry.CommandLine ?? '' }));
}
