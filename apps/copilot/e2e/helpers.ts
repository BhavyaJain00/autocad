/**
 * Helpers for CAD Copilot's Electron end-to-end tests: launching the built app with isolated
 * user-data and connector folders, calling the preload bridge from the page, and a fake CAD
 * application (a real connector server in the test process) for the app to connect to.
 */
import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { _electron as electron, type ElectronApplication, type Page } from '@playwright/test';
import {
  BRIDGE_PROTOCOL_VERSION,
  type DocumentInfo,
  type HelloResult,
  type ToolCallParams,
  type ToolResult,
  type WindowBounds,
} from '@cadstudio/cad-protocol';
import { startConnectorServer, type ConnectorServer } from '@cadstudio/cad-protocol/node';
import type { CopilotBridge, CopilotSnapshot } from '../src/shared/bridge';

/** apps/copilot: Electron loads package.json "main" (dist/electron/main.cjs) from here. */
export const appDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** The page's global object as seen by code passed to page.evaluate(). */
export type BridgeGlobal = typeof globalThis & { copilot: CopilotBridge };

/**
 * True unless E2E_VISIBLE=1: the apps under test run in hidden mode (CADCOPILOT_HIDDEN=1 /
 * CADSTUDIO_HIDDEN=1), so no window ever appears on the screen (docs/testing/hidden-windows.md).
 */
export const HIDDEN_WINDOWS = process.env.E2E_VISIBLE !== '1';

// Set in this process too, so every launch path (including CAD Studio launched by
// cadstudio.ts and second instances) inherits hidden mode.
if (HIDDEN_WINDOWS) {
  process.env.CADCOPILOT_HIDDEN = '1';
  process.env.CADSTUDIO_HIDDEN = '1';
} else {
  delete process.env.CADCOPILOT_HIDDEN;
  delete process.env.CADSTUDIO_HIDDEN;
}

/** Variables of the test environment that must never leak into the app under test. */
const BLOCKED_VARIABLES: ReadonlySet<string> = new Set([
  // Set by VS Code's extension host: would start Electron as plain Node.
  'ELECTRON_RUN_AS_NODE',
  // Would load a dev server instead of the built renderer.
  'VITE_DEV_SERVER_URL',
  // A real key would make "no API key" assertions depend on the machine.
  'ANTHROPIC_API_KEY',
  'CADCOPILOT_USER_DATA',
  'CADCOPILOT_HOME',
  'CADCOPILOT_CONNECT',
  'CADCOPILOT_SCRIPT',
  'CADCOPILOT_E2E',
]);

export interface LaunchOptions {
  /** The app's userData folder (settings, credentials). */
  readonly userDataDir: string;
  /** CADCOPILOT_HOME: the folder connectors advertise themselves in. */
  readonly homeDir: string;
  /** Extra environment variables (CADCOPILOT_SCRIPT, CADCOPILOT_CONNECT, ...). */
  readonly env?: Readonly<Record<string, string>>;
}

export interface LaunchedApp {
  readonly app: ElectronApplication;
  /** The copilot window. */
  readonly page: Page;
}

/** A new empty folder in the system temp directory. */
export function createTempDir(label: string): string {
  return mkdtempSync(path.join(os.tmpdir(), `cadcopilot-e2e-${label}-`));
}

/** Removes a folder created with {@link createTempDir}; never throws. */
export function removeTempDir(directory: string | undefined): void {
  if (directory === undefined) return;
  try {
    rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  } catch {
    // Best effort: it is a small folder in the temp directory.
  }
}

/** The environment of an app under test: this process's, cleaned, plus the isolation variables. */
export function launchEnvironment(options: LaunchOptions): Record<string, string> {
  const env: Record<string, string> = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (value !== undefined && !BLOCKED_VARIABLES.has(key.toUpperCase())) env[key] = value;
  }
  return {
    ...env,
    CADCOPILOT_E2E: '1',
    CADCOPILOT_USER_DATA: options.userDataDir,
    CADCOPILOT_HOME: options.homeDir,
    ...(HIDDEN_WINDOWS ? { CADCOPILOT_HIDDEN: '1', CADSTUDIO_HIDDEN: '1' } : {}),
    ...options.env,
  };
}

/**
 * Electron child processes by app. Captured at launch: Playwright's `app.process()` throws once
 * the application object has been disposed after the process exited.
 */
const childProcesses = new WeakMap<ElectronApplication, ChildProcess>();

function childOf(app: ElectronApplication): ChildProcess | undefined {
  return childProcesses.get(app);
}

function hasExited(app: ElectronApplication): boolean {
  const child = childOf(app);
  return child === undefined || child.exitCode !== null || child.signalCode !== null;
}

/** Resolves when the Electron process has exited (immediately if it already has). */
export function waitForExit(app: ElectronApplication): Promise<void> {
  const child = childOf(app);
  if (child === undefined || hasExited(app)) return Promise.resolve();
  return new Promise((resolve) => {
    child.once('exit', () => resolve());
  });
}

/** Launches the built app in an isolated environment and waits for the window's DOM. */
export async function launchCopilot(options: LaunchOptions): Promise<LaunchedApp> {
  const app = await electron.launch({ args: [appDir], cwd: appDir, env: launchEnvironment(options) });
  childProcesses.set(app, app.process());
  try {
    const page = await app.firstWindow();
    await page.waitForLoadState('domcontentloaded');
    return { app, page };
  } catch (error) {
    await closeApp(app);
    throw error;
  }
}

/**
 * Kills a process and everything it started. On Windows a plain kill() would leave Electron's
 * helper processes behind.
 */
function killProcessTree(child: ChildProcess | undefined): void {
  if (child === undefined || child.exitCode !== null || child.signalCode !== null) return;
  if (process.platform === 'win32' && child.pid !== undefined) {
    const result = spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
    if (result.status === 0) return;
  }
  child.kill();
}

/** True when `promise` settles within `timeoutMs`. */
async function settlesWithin(promise: Promise<void>, timeoutMs: number): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timedOut = new Promise<boolean>((resolve) => {
    timer = setTimeout(() => resolve(false), timeoutMs);
  });
  try {
    return await Promise.race([promise.then(() => true), timedOut]);
  } finally {
    clearTimeout(timer);
  }
}

/** Ends the app immediately (no shutdown work), killing the process tree if it does not exit. */
export async function closeApp(app: ElectronApplication | undefined): Promise<void> {
  if (app === undefined || hasExited(app)) return;
  const exited = waitForExit(app);
  await app
    .evaluate(({ app: electronApp }) => {
      setTimeout(() => electronApp.exit(0), 0);
    })
    .catch(() => undefined);
  if (!(await settlesWithin(exited, 10_000))) {
    killProcessTree(childOf(app));
    await exited;
  }
}

/**
 * Closes the app the way the user does (the header's close button): the window closes, the app
 * restores the CAD window, saves its settings and quits. Returns false (after killing the
 * process) when it did not exit within `timeoutMs`.
 */
export async function quitThroughWindowClose(launched: LaunchedApp, timeoutMs = 15_000): Promise<boolean> {
  const exited = waitForExit(launched.app);
  await launched.page
    .evaluate(() => {
      (globalThis as unknown as BridgeGlobal).copilot.closeWindow();
    })
    .catch(() => undefined);
  if (await settlesWithin(exited, timeoutMs)) return true;
  killProcessTree(childOf(launched.app));
  await exited;
  return false;
}

/** The Electron binary of this repository (in plain Node the `electron` package exports its path). */
function electronBinary(): string {
  return createRequire(import.meta.url)('electron') as string;
}

/**
 * Starts another instance of the app directly (not through Playwright) and resolves with its
 * exit code. Used for the single-instance behaviour: a second launch must exit on its own.
 * The process tree is killed when it is still running after `timeoutMs` (the result is null).
 */
export function runSecondInstance(options: LaunchOptions, timeoutMs = 20_000): Promise<number | null> {
  return new Promise((resolve, reject) => {
    const child = spawn(electronBinary(), [appDir], {
      cwd: appDir,
      env: launchEnvironment(options),
      stdio: 'ignore',
      windowsHide: HIDDEN_WINDOWS,
    });
    const timer = setTimeout(() => {
      killProcessTree(child);
      resolve(null);
    }, timeoutMs);
    child.once('error', (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.once('exit', (code) => {
      clearTimeout(timer);
      resolve(code);
    });
  });
}

/** Names of the bridge members that are functions returning a promise. */
export type AsyncBridgeMethod = {
  [K in keyof CopilotBridge]: CopilotBridge[K] extends (...args: never[]) => Promise<unknown> ? K : never;
}[keyof CopilotBridge];

/** Calls a promise-returning bridge method in the page and returns its result. */
export async function callBridge<K extends AsyncBridgeMethod>(
  page: Page,
  method: K,
  ...args: Parameters<CopilotBridge[K]>
): Promise<Awaited<ReturnType<CopilotBridge[K]>>> {
  const result = await page.evaluate(
    ([name, callArgs]) => {
      const bridge = (globalThis as unknown as BridgeGlobal).copilot;
      const fn = bridge[name] as (...values: unknown[]) => Promise<unknown>;
      return fn(...callArgs);
    },
    [method, args as unknown[]] as const,
  );
  return result as Awaited<ReturnType<CopilotBridge[K]>>;
}

/** Calls a bridge method with arbitrary (possibly invalid) arguments, as a hostile page could. */
export async function callBridgeRaw(page: Page, method: keyof CopilotBridge, ...args: unknown[]): Promise<unknown> {
  return page.evaluate(
    ([name, callArgs]) => {
      const bridge = (globalThis as unknown as BridgeGlobal).copilot as unknown as Record<string, (...values: unknown[]) => unknown>;
      return bridge[name]?.(...callArgs);
    },
    [method, args] as const,
  );
}

export function getSnapshot(page: Page): Promise<CopilotSnapshot> {
  return callBridge(page, 'getSnapshot');
}

/** What the page-side recorder collected from the bridge's push channels. */
export interface RecordedEvents {
  timeline: { type: string; seq: number }[];
  connection: { status: string }[];
  connectors: { id: string }[][];
  settings: Record<string, unknown>[];
  busy: boolean[];
  /** typeof of what each on*() returned. */
  unsubscribeTypes: string[];
}

type RecorderGlobal = BridgeGlobal & { __copilotRecorder?: RecordedEvents & { unsubscribe: (() => void)[] } };

/** Subscribes to every push channel of the bridge in the page and records what arrives. */
export async function startRecording(page: Page): Promise<void> {
  await page.evaluate(() => {
    const scope = globalThis as unknown as RecorderGlobal;
    const recorder: NonNullable<RecorderGlobal['__copilotRecorder']> = {
      timeline: [],
      connection: [],
      connectors: [],
      settings: [],
      busy: [],
      unsubscribeTypes: [],
      unsubscribe: [],
    };
    recorder.unsubscribe = [
      scope.copilot.onTimeline((entry) => recorder.timeline.push(entry)),
      scope.copilot.onConnection((connection) => recorder.connection.push(connection)),
      scope.copilot.onConnectors((connectors) => recorder.connectors.push(connectors)),
      scope.copilot.onSettings((settings) => recorder.settings.push({ ...settings })),
      scope.copilot.onBusy((busy) => recorder.busy.push(busy)),
    ];
    recorder.unsubscribeTypes = recorder.unsubscribe.map((unsubscribe) => typeof unsubscribe);
    scope.__copilotRecorder = recorder;
  });
}

/** What the recorder started with {@link startRecording} has collected so far. */
export async function recordedEvents(page: Page): Promise<RecordedEvents> {
  return page.evaluate(() => {
    const recorder = (globalThis as unknown as RecorderGlobal).__copilotRecorder;
    if (recorder === undefined) throw new Error('startRecording() was not called');
    const { unsubscribe: _unsubscribe, ...events } = recorder;
    return events;
  });
}

/** Unsubscribes the recorder from every channel (it keeps what it collected). */
export async function stopRecording(page: Page): Promise<void> {
  await page.evaluate(() => {
    const recorder = (globalThis as unknown as RecorderGlobal).__copilotRecorder;
    for (const unsubscribe of recorder?.unsubscribe ?? []) unsubscribe();
  });
}

/** Bounds (DIP) and state of the copilot window, read in the main process. */
export interface WindowInfo {
  bounds: { x: number; y: number; width: number; height: number };
  minimumSize: number[];
  alwaysOnTop: boolean;
  title: string;
  visible: boolean;
}

export async function windowInfo(app: ElectronApplication): Promise<WindowInfo> {
  return app.evaluate(({ BrowserWindow }) => {
    const win = BrowserWindow.getAllWindows()[0];
    if (win === undefined) throw new Error('The app has no window');
    return {
      bounds: win.getBounds(),
      minimumSize: win.getMinimumSize(),
      alwaysOnTop: win.isAlwaysOnTop(),
      title: win.getTitle(),
      visible: win.isVisible(),
    };
  });
}

/**
 * Saves a PNG of the copilot window's page to `filePath` (works in hidden mode: the window keeps
 * painting) and returns the image size in pixels.
 */
export async function captureWindow(app: ElectronApplication, filePath: string): Promise<{ width: number; height: number }> {
  const { base64, width, height } = await app.evaluate(async ({ BrowserWindow }) => {
    const win = BrowserWindow.getAllWindows()[0];
    if (win === undefined) throw new Error('The app has no window to capture.');
    const image = await win.webContents.capturePage();
    const size = image.getSize();
    return { base64: image.toPNG().toString('base64'), width: size.width, height: size.height };
  });
  mkdirSync(path.dirname(filePath), { recursive: true });
  writeFileSync(filePath, Buffer.from(base64, 'base64'));
  return { width, height };
}

/** Number of open BrowserWindows. */
export async function windowCount(app: ElectronApplication): Promise<number> {
  return app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length);
}

// ---------------------------------------------------------------------------------------------
// Fake CAD
// ---------------------------------------------------------------------------------------------

export interface FakeCad {
  /** Advert id of the fake ('cadstudio-<pid of the test process>'). */
  readonly id: string;
  readonly displayName: string;
  /** Every tools.call received, in order. */
  readonly toolCalls: ToolCallParams[];
  /** Every window.setBounds received, in order (physical pixels). */
  readonly setBoundsCalls: WindowBounds[];
  /** The fake window's bounds before anything moved it (physical pixels). */
  readonly initialBounds: WindowBounds;
  /** The drawing the fake reports. */
  document(): DocumentInfo;
  /** Number of authenticated clients. */
  clientCount(): number;
  /** Announces the shutdown, closes the pipe and removes the advert. */
  close(): Promise<void>;
}

export const FAKE_CAD_TOOLS = ['get_drawing_info', 'create_line'];

/**
 * Starts a fake CAD application in this process: a real connector server (named pipe, advert in
 * `homeDir`, token auth) whose handlers record what they receive. `get_drawing_info` reads,
 * `create_line` "creates" an entity, bumps the revision and notifies `document.changed`.
 */
export async function startFakeCad(homeDir: string): Promise<FakeCad> {
  const previousHome = process.env.CADCOPILOT_HOME;
  process.env.CADCOPILOT_HOME = homeDir;

  const displayName = 'Fake CAD';
  const toolCalls: ToolCallParams[] = [];
  const setBoundsCalls: WindowBounds[] = [];
  const initialBounds: WindowBounds = { x: 120, y: 90, width: 900, height: 600 };
  let bounds: WindowBounds = { ...initialBounds };
  const document: DocumentInfo = {
    id: 'doc-e2e',
    name: 'E2E Plan.cadstudio',
    path: null,
    unit: 'mm',
    revision: 1,
    entityCount: 0,
  };
  let server: ConnectorServer | undefined;

  const result = (overrides: Partial<ToolResult>): ToolResult => ({
    success: true,
    createdIds: [],
    modifiedIds: [],
    deletedIds: [],
    warnings: [],
    errors: [],
    revision: document.revision,
    ...overrides,
  });

  try {
    server = await startConnectorServer({
      cad: 'cadstudio',
      displayName,
      cadVersion: '9.9.9',
      connectorVersion: '1.0.0',
      handlers: {
        hello: async (): Promise<HelloResult> => ({
          cad: 'cadstudio',
          cadVersion: '9.9.9',
          connectorVersion: '1.0.0',
          protocolVersion: BRIDGE_PROTOCOL_VERSION,
          document: { ...document },
          supportedTools: [...FAKE_CAD_TOOLS],
        }),
        callTool: async (params): Promise<ToolResult> => {
          toolCalls.push(params);
          if (params.name === 'get_drawing_info') {
            return result({ data: { name: document.name, path: null, unit: 'mm', entityCount: document.entityCount, revision: document.revision } });
          }
          if (params.name === 'create_line') {
            document.revision += 1;
            document.entityCount += 1;
            const id = `line-${document.entityCount}`;
            server?.notify('document.changed', { documentId: document.id, revision: document.revision });
            return result({ data: { entities: [{ id, type: 'line' }] }, createdIds: [id] });
          }
          return result({ success: false, errors: [`The fake CAD does not implement ${params.name}.`] });
        },
        getWindowBounds: async () => ({ ...bounds }),
        setWindowBounds: async (next) => {
          setBoundsCalls.push({ ...next });
          bounds = { ...next };
        },
        focusWindow: async () => undefined,
      },
    });
  } finally {
    // Only the advert/pipe names depend on the variable, and they are fixed by now.
    if (previousHome === undefined) delete process.env.CADCOPILOT_HOME;
    else process.env.CADCOPILOT_HOME = previousHome;
  }

  const running = server;
  let closed = false;
  return {
    id: running.advert.id,
    displayName,
    toolCalls,
    setBoundsCalls,
    initialBounds,
    document: () => ({ ...document }),
    clientCount: () => running.clientCount,
    close: async () => {
      if (closed) return;
      closed = true;
      await running.close();
    },
  };
}
