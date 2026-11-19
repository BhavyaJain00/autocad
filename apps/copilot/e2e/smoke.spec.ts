/**
 * Smoke tests of the built CAD Copilot app (dist/): the window and its preload bridge, settings
 * persistence, and the whole connector flow against a fake CAD application with the scripted
 * test model (no network, no real CAD).
 */
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type ElectronApplication } from '@playwright/test';
import type { WindowBounds } from '@cadstudio/cad-protocol';
import { WINDOWS_INVISIBLE_FRAME } from '../src/main/docking';
import {
  PANEL_WIDTH_DEFAULT,
  PANEL_WIDTH_MAX,
  PANEL_WIDTH_MIN,
  type ActionResult,
  type CopilotSettings,
  type TimelineEntry,
} from '../src/shared/bridge';
import {
  callBridge,
  callBridgeRaw,
  closeApp,
  createTempDir,
  FAKE_CAD_TOOLS,
  getSnapshot,
  HIDDEN_WINDOWS,
  launchCopilot,
  quitThroughWindowClose,
  recordedEvents,
  removeTempDir,
  runSecondInstance,
  startFakeCad,
  startRecording,
  stopRecording,
  windowCount,
  windowInfo,
  type BridgeGlobal,
  type FakeCad,
  type LaunchedApp,
} from './helpers';

/** Every member of window.copilot (the CopilotBridge contract), sorted. */
const BRIDGE_MEMBERS = [
  'answerQuestion',
  'closeWindow',
  'connect',
  'disconnect',
  'getSnapshot',
  'minimizeWindow',
  'onBusy',
  'onConnection',
  'onConnectors',
  'onSettings',
  'onTimeline',
  'openExternal',
  'pickAttachments',
  'refreshConnectors',
  'resetSession',
  'resolveApproval',
  'sendMessage',
  'setApiKey',
  'setDocked',
  'stop',
  'updateSettings',
];

const DEFAULT_SETTINGS: CopilotSettings = {
  model: 'claude-opus-5-5',
  effort: 'high',
  approvalMode: 'ask-destructive',
  panelWidth: PANEL_WIDTH_DEFAULT,
  alwaysOnTop: false,
  autoDock: true,
  autoConnect: true,
  hasApiKey: false,
  testModel: false,
};

/** Pixel tolerance for window geometry (DPI rounding). */
const TOLERANCE = 2;

let launched: LaunchedApp | undefined;
let fakeCad: FakeCad | undefined;
let userDataDir: string;
let homeDir: string;

async function start(env: Record<string, string> = {}): Promise<LaunchedApp> {
  launched = await launchCopilot({ userDataDir, homeDir, env });
  await expect(launched.page.locator('[data-testid="copilot-root"]')).toBeVisible();
  return launched;
}

/** Writes settings.json before the app starts (missing fields take their defaults). */
function presetSettings(settings: Record<string, unknown>): void {
  mkdirSync(userDataDir, { recursive: true });
  writeFileSync(path.join(userDataDir, 'settings.json'), JSON.stringify({ version: 1, ...settings }));
}

function readStoredSettings(): Record<string, unknown> | null {
  const file = path.join(userDataDir, 'settings.json');
  if (!existsSync(file)) return null;
  try {
    return JSON.parse(readFileSync(file, 'utf8')) as Record<string, unknown>;
  } catch {
    // Being replaced right now.
    return null;
  }
}

/** A physical-pixel rectangle converted to DIP by the app's main process. */
async function toDip(app: ElectronApplication, rect: WindowBounds): Promise<WindowBounds> {
  return app.evaluate(
    ({ screen }, physical) => (typeof screen.screenToDipRect === 'function' ? screen.screenToDipRect(null, physical) : physical),
    rect,
  );
}

/** Work area (DIP) of the display a physical-pixel rectangle is on. */
async function workAreaOf(app: ElectronApplication, rect: WindowBounds): Promise<WindowBounds> {
  return app.evaluate(({ screen }, physical) => {
    const dip = typeof screen.screenToDipRect === 'function' ? screen.screenToDipRect(null, physical) : physical;
    return screen.getDisplayMatching(dip).workArea;
  }, rect);
}

test.beforeEach(() => {
  userDataDir = createTempDir('user-data');
  homeDir = createTempDir('home');
});

test.afterEach(async () => {
  await closeApp(launched?.app);
  launched = undefined;
  await fakeCad?.close().catch(() => undefined);
  fakeCad = undefined;
  removeTempDir(userDataDir);
  removeTempDir(homeDir);
});

test('the window opens with a hardened renderer and the exact bridge contract', async () => {
  const { app, page } = await start();

  // --- The bridge: exactly the contract members, all functions ---
  const members = await page.evaluate(() => {
    const bridge = (globalThis as unknown as BridgeGlobal).copilot as unknown as Record<string, unknown>;
    return Object.keys(bridge)
      .sort()
      .map((key) => [key, typeof bridge[key]] as const);
  });
  expect(members).toEqual(BRIDGE_MEMBERS.map((name) => [name, 'function']));

  // --- The snapshot: defaults ---
  const snapshot = await getSnapshot(page);
  expect(snapshot).toEqual({
    appVersion: '0.1.0',
    settings: DEFAULT_SETTINGS,
    connection: { status: 'disconnected' },
    connectors: [],
    busy: false,
    timeline: [],
  });

  // --- No Node or Electron in the page ---
  const globals = await page.evaluate(() => {
    const scope = globalThis as Record<string, unknown>;
    return ['require', 'process', 'module', 'Buffer', 'global', 'ipcRenderer', 'electron', '__dirname'].map(
      (name) => [name, typeof scope[name]] as const,
    );
  });
  for (const [name, type] of globals) expect(type, name).toBe('undefined');

  // --- The window ---
  expect(await windowCount(app)).toBe(1);
  // Shown once the first paint is ready (never in hidden mode, the default for tests).
  await expect.poll(async () => (await windowInfo(app)).visible).toBe(!HIDDEN_WINDOWS);
  const info = await windowInfo(app);
  expect(info.title).toBe('CAD Copilot');
  expect(info.bounds.width).toBe(PANEL_WIDTH_DEFAULT);
  expect(info.minimumSize).toEqual([PANEL_WIDTH_MIN, 520]);
  expect(info.alwaysOnTop).toBe(false);

  const main = await app.evaluate(({ app: electronApp, BrowserWindow, Menu }) => {
    const win = BrowserWindow.getAllWindows()[0];
    // Not in Electron's typings: the web preferences the window was actually created with.
    const contents = win?.webContents as unknown as { getLastWebPreferences(): Record<string, unknown> | null } | undefined;
    const preferences = contents?.getLastWebPreferences();
    return {
      name: electronApp.getName(),
      userData: electronApp.getPath('userData'),
      hasMenu: Menu.getApplicationMenu() !== null,
      contextIsolation: preferences?.contextIsolation,
      sandbox: preferences?.sandbox,
      nodeIntegration: preferences?.nodeIntegration,
      webSecurity: preferences?.webSecurity,
      url: win?.webContents.getURL() ?? '',
    };
  });
  expect(main.name).toBe('CAD Copilot');
  expect(path.resolve(main.userData)).toBe(path.resolve(userDataDir));
  expect(main.hasMenu).toBe(false);
  expect(main.contextIsolation).toBe(true);
  expect(main.sandbox).toBe(true);
  expect(main.nodeIntegration).toBe(false);
  expect(main.webSecurity).toBe(true);
  expect(main.url.startsWith('file://')).toBe(true);

  // --- The built page carries the Content Security Policy ---
  const csp = await page.evaluate(() => {
    const scope = globalThis as unknown as {
      document: { querySelector(selector: string): { getAttribute(name: string): string | null } | null };
    };
    return scope.document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.getAttribute('content') ?? '';
  });
  expect(csp).toContain("default-src 'none'");
  expect(csp).toContain("script-src 'self'");
  expect(csp).toContain("connect-src 'self'");

  // --- openExternal: https only (shell.openExternal is replaced so no browser opens) ---
  await app.evaluate(({ shell }) => {
    const scope = globalThis as typeof globalThis & { __openedUrls?: string[] };
    scope.__openedUrls = [];
    shell.openExternal = async (url: string) => {
      scope.__openedUrls?.push(url);
    };
  });
  for (const url of [
    'file:///C:/Windows/System32/calc.exe',
    'http://example.com/',
    'javascript:alert(1)',
    'https://console.anthropic.com/settings/keys',
  ]) {
    await callBridgeRaw(page, 'openExternal', url);
  }
  await callBridgeRaw(page, 'openExternal', { href: 'https://example.com/' });
  await expect
    .poll(() => app.evaluate(() => (globalThis as typeof globalThis & { __openedUrls?: string[] }).__openedUrls ?? []))
    .toEqual(['https://console.anthropic.com/settings/keys']);

  // --- Invalid IPC arguments are refused by the main process ---
  expect(((await callBridgeRaw(page, 'sendMessage', '   ', [])) as ActionResult).ok).toBe(false);
  expect(((await callBridgeRaw(page, 'sendMessage', { text: 'x' }, 'nope')) as ActionResult).ok).toBe(false);
  expect(((await callBridgeRaw(page, 'connect', '..\\..\\pipe\\evil')) as ActionResult).ok).toBe(false);
  expect(((await callBridgeRaw(page, 'connect', 'cadstudio-999999')) as ActionResult).ok).toBe(false);
  expect(((await callBridgeRaw(page, 'setDocked', 'yes')) as ActionResult).ok).toBe(false);
  expect(((await callBridgeRaw(page, 'setApiKey', 12345)) as ActionResult).ok).toBe(false);
  expect(await callBridgeRaw(page, 'updateSettings', { effort: 'bogus', alwaysOnTop: true })).toEqual(DEFAULT_SETTINGS);
  // Docking needs a connection.
  expect(((await callBridge(page, 'setDocked', true)) as ActionResult).ok).toBe(false);
  // One-way calls with nothing to act on are harmless.
  await callBridgeRaw(page, 'stop');
  await callBridgeRaw(page, 'answerQuestion', 'unknown-request', [{ questionId: 'q', answer: 'a' }]);
  await callBridgeRaw(page, 'resolveApproval', 'unknown-request', 'approve');
  await callBridgeRaw(page, 'disconnect');
  const after = await getSnapshot(page);
  expect(after.settings).toEqual(DEFAULT_SETTINGS);
  expect(after.timeline).toEqual([]);
  expect(after.busy).toBe(false);
  // The failed connect to a connector that is not running is the only trace, cleared by disconnect.
  expect(after.connection).toEqual({ status: 'disconnected' });

  // --- Nothing is written until something changes ---
  expect(existsSync(path.join(userDataDir, 'settings.json'))).toBe(false);
  expect(existsSync(path.join(userDataDir, 'credentials.json'))).toBe(false);
});

test('blocks new windows and navigation away from the app', async () => {
  const { app, page } = await start();
  const mainFrameUrl = (): Promise<string> =>
    app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.webContents.getURL() ?? '');
  const appUrl = await mainFrameUrl();
  expect(appUrl.startsWith('file://')).toBe(true);

  const openedWindow = await page.evaluate(() => {
    const scope = globalThis as unknown as { open(url: string): unknown };
    return scope.open('https://example.invalid/');
  });
  expect(openedWindow).toBeNull();

  await page.evaluate(() => {
    (globalThis as unknown as { location: { href: string } }).location.href = 'https://example.invalid/';
  });
  // Nothing observable happens when a navigation is blocked; give it time to (not) happen.
  // (From here on the page is checked from the main process: Playwright keeps waiting for the
  // navigation it saw being requested.)
  await page.waitForTimeout(750);
  expect(await mainFrameUrl()).toBe(appUrl);
  expect(await windowCount(app)).toBe(1);
  const stillRendered = await app.evaluate(({ BrowserWindow }) =>
    BrowserWindow.getAllWindows()[0]?.webContents.executeJavaScript(
      `document.querySelector('[data-testid="copilot-root"]') !== null && typeof window.copilot.getSnapshot === 'function'`,
    ),
  );
  expect(stillRendered).toBe(true);
});

test('settings changes apply, are pushed to the renderer and persist across a relaunch', async () => {
  const first = await start();
  await startRecording(first.page);

  const patch = {
    model: 'claude-sonnet-5-5',
    effort: 'low',
    approvalMode: 'ask-all',
    panelWidth: 520,
    alwaysOnTop: true,
    autoDock: false,
    autoConnect: false,
  } as const;
  const expected: CopilotSettings = { ...DEFAULT_SETTINGS, ...patch };

  expect(await callBridge(first.page, 'updateSettings', patch)).toEqual(expected);
  expect((await getSnapshot(first.page)).settings).toEqual(expected);

  // Pushed on the settings channel.
  await expect.poll(async () => (await recordedEvents(first.page)).settings.at(-1)).toEqual(expected);
  expect((await recordedEvents(first.page)).unsubscribeTypes).toEqual(['function', 'function', 'function', 'function', 'function']);

  // Applied to the window.
  await expect.poll(async () => (await windowInfo(first.app)).bounds.width).toBe(520);
  expect((await windowInfo(first.app)).alwaysOnTop).toBe(true);

  // An out-of-range width is clamped, not rejected.
  expect((await callBridge(first.page, 'updateSettings', { panelWidth: 5000 })).panelWidth).toBe(PANEL_WIDTH_MAX);
  expect((await callBridge(first.page, 'updateSettings', { panelWidth: 520 })).panelWidth).toBe(520);

  // After unsubscribing, nothing more is recorded.
  await stopRecording(first.page);
  const recordedBefore = (await recordedEvents(first.page)).settings.length;
  await callBridge(first.page, 'updateSettings', { effort: 'medium' });
  await callBridge(first.page, 'updateSettings', { effort: 'low' });
  expect((await recordedEvents(first.page)).settings).toHaveLength(recordedBefore);

  // Closing the window quits the app, which saves the settings first.
  expect(await quitThroughWindowClose(first)).toBe(true);
  launched = undefined;
  expect(readStoredSettings()).toMatchObject(patch);

  const second = await start();
  expect((await getSnapshot(second.page)).settings).toEqual(expected);
  const info = await windowInfo(second.app);
  expect(info.bounds.width).toBe(520);
  expect(info.alwaysOnTop).toBe(true);
});

test('connects to a CAD, runs a scripted turn through its tools, docks and notices the CAD closing', async () => {
  // No automatic connect or dock: every step below is explicit.
  presetSettings({ autoConnect: false, autoDock: false });
  const scriptPath = path.join(homeDir, 'script.json');
  writeFileSync(
    scriptPath,
    JSON.stringify({
      turns: [
        {
          toolCalls: [
            { name: 'get_drawing_info', input: {} },
            { name: 'create_line', input: { start: { x: 0, y: 0 }, end: { x: 1000, y: 0 } } },
          ],
        },
        { text: 'The line is drawn.' },
      ],
    }),
  );

  const cad = await startFakeCad(homeDir);
  fakeCad = cad;
  const { app, page } = await start({ CADCOPILOT_SCRIPT: scriptPath });
  await startRecording(page);

  // --- Discovery ---
  const initial = await getSnapshot(page);
  expect(initial.settings.testModel).toBe(true);
  expect(initial.connection).toEqual({ status: 'disconnected' });
  const expectedSummary = { id: cad.id, cad: 'cadstudio', displayName: 'Fake CAD', cadVersion: '9.9.9', pid: process.pid };
  expect(await callBridge(page, 'refreshConnectors')).toEqual([expectedSummary]);
  expect((await getSnapshot(page)).connectors).toEqual([expectedSummary]);

  // --- Connect ---
  expect(await callBridge(page, 'connect', cad.id)).toEqual({ ok: true });
  expect((await getSnapshot(page)).connection).toEqual({
    status: 'connected',
    connector: expectedSummary,
    document: { id: 'doc-e2e', name: 'E2E Plan.cadstudio', path: null, unit: 'mm', revision: 1, entityCount: 0 },
    supportedTools: FAKE_CAD_TOOLS,
    docked: false,
  });
  expect(cad.clientCount()).toBe(1);
  await expect
    .poll(async () => (await recordedEvents(page)).connection.map((state) => state.status))
    .toEqual(['connecting', 'connected']);

  // --- A turn with the scripted model: a read tool, a write tool, then closing text ---
  expect(await callBridge(page, 'sendMessage', 'Draw a 1 m line', [])).toEqual({ ok: true });
  await expect
    .poll(async () => (await getSnapshot(page)).timeline.map((entry) => entry.type), { timeout: 30_000 })
    .toContain('turn.finished');
  await expect.poll(async () => (await getSnapshot(page)).busy).toBe(false);

  const timeline = (await getSnapshot(page)).timeline;
  expect(timeline.map((entry) => entry.type)).toEqual([
    'user.message',
    'tool.started',
    'tool.finished',
    'tool.started',
    'tool.finished',
    'message.done',
    'turn.finished',
  ]);
  expect(timeline.map((entry) => entry.seq)).toEqual([...timeline.map((entry) => entry.seq)].sort((a, b) => a - b));
  const byType = <T extends TimelineEntry['type']>(type: T): Extract<TimelineEntry, { type: T }>[] =>
    timeline.filter((entry): entry is Extract<TimelineEntry, { type: T }> => entry.type === type);
  expect(byType('user.message')[0]).toMatchObject({ text: 'Draw a 1 m line', attachments: [] });
  expect(byType('tool.started').map((entry) => entry.name)).toEqual(['get_drawing_info', 'create_line']);
  const finishedTools = byType('tool.finished');
  expect(finishedTools.map((entry) => entry.name)).toEqual(['get_drawing_info', 'create_line']);
  expect(finishedTools.map((entry) => entry.result?.success)).toEqual([true, true]);
  expect(finishedTools[1]?.result?.createdIds).toEqual(['line-1']);
  expect(byType('message.done')[0]?.text).toBe('The line is drawn.');
  expect(byType('turn.finished')[0]).toMatchObject({ stopReason: 'end_turn' });

  // The live stream delivered the same entries (plus the streaming deltas).
  const live = await recordedEvents(page);
  const liveTypes = live.timeline.map((entry) => entry.type);
  expect(liveTypes.filter((type) => type !== 'message.delta')).toEqual(timeline.map((entry) => entry.type));
  expect(liveTypes).toContain('message.delta');
  expect(live.busy).toEqual([true, false]);

  // The fake CAD received the calls in order, attributed to the AI, with the user's instruction.
  expect(cad.toolCalls.map((call) => call.name)).toEqual(['get_drawing_info', 'create_line']);
  for (const call of cad.toolCalls) {
    expect(call.source).toBe('ai');
    expect(call.instruction).toBe('Draw a 1 m line');
    expect(call.requestId.length).toBeGreaterThan(0);
    expect(call.operationId.length).toBeGreaterThan(0);
  }
  expect(cad.toolCalls[1]?.arguments).toEqual({ start: { x: 0, y: 0 }, end: { x: 1000, y: 0 } });
  // The write call carries the revision the model last saw; the read call does not need one.
  expect(cad.toolCalls[1]?.expectedRevision).toBe(1);
  expect(new Set(cad.toolCalls.map((call) => call.requestId)).size).toBe(2);

  // The CAD's document.changed notification refreshed the connection state.
  await expect
    .poll(async () => {
      const { connection } = await getSnapshot(page);
      return connection.status === 'connected' ? connection.document : null;
    })
    .toMatchObject({ revision: 2, entityCount: 1 });

  // --- Dock: the CAD takes the left part of its display, the copilot the right column ---
  const workArea = await workAreaOf(app, cad.initialBounds);
  expect(await callBridge(page, 'setDocked', true)).toEqual({ ok: true });
  expect(cad.setBoundsCalls).toHaveLength(1);
  const cadDocked = await toDip(app, cad.setBoundsCalls[0] as WindowBounds);
  const panel = (await windowInfo(app)).bounds;

  // On Windows the CAD's outer bounds include its invisible border (left, right, bottom).
  const inset = process.platform === 'win32' ? WINDOWS_INVISIBLE_FRAME : 0;
  expect(Math.abs(cadDocked.x + inset - workArea.x)).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(cadDocked.y - workArea.y)).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(cadDocked.height - inset - workArea.height)).toBeLessThanOrEqual(TOLERANCE);
  expect(cadDocked.width).toBeGreaterThanOrEqual(400);
  // The copilot window sits directly to the right of the CAD's visible frame, at full work-area height.
  expect(Math.abs(panel.x - (cadDocked.x + cadDocked.width - inset))).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(panel.x + panel.width - (workArea.x + workArea.width))).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(panel.y - workArea.y)).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(panel.height - workArea.height)).toBeLessThanOrEqual(TOLERANCE);
  expect(panel.width).toBeGreaterThanOrEqual(PANEL_WIDTH_MIN);
  expect(panel.width).toBeLessThanOrEqual(PANEL_WIDTH_MAX);
  expect((await getSnapshot(page)).connection).toMatchObject({ status: 'connected', docked: true });

  // --- Undock: the CAD gets its previous bounds back ---
  expect(await callBridge(page, 'setDocked', false)).toEqual({ ok: true });
  expect(cad.setBoundsCalls).toHaveLength(2);
  expect(cad.setBoundsCalls[1]).toEqual(cad.initialBounds);
  expect((await getSnapshot(page)).connection).toMatchObject({ status: 'connected', docked: false });
  expect((await windowInfo(app)).bounds.width).toBe(PANEL_WIDTH_DEFAULT);

  // --- A new conversation clears the timeline ---
  await callBridgeRaw(page, 'resetSession');
  await expect.poll(async () => (await getSnapshot(page)).timeline.map((entry) => entry.type)).toEqual(['session.reset']);

  // --- The CAD closes ---
  await cad.close();
  await expect.poll(async () => (await getSnapshot(page)).connection).toEqual({ status: 'disconnected' });
  await expect.poll(async () => (await callBridge(page, 'refreshConnectors')).length).toBe(0);
  expect((await recordedEvents(page)).connection.at(-1)).toEqual({ status: 'disconnected' });
  // It stays disconnected: the connector announced its shutdown.
  await page.waitForTimeout(2500);
  expect((await getSnapshot(page)).connection).toEqual({ status: 'disconnected' });
});

test('with the default settings it connects and docks by itself and restores the CAD window on quit', async () => {
  const cad = await startFakeCad(homeDir);
  fakeCad = cad;
  const started = await start({ CADCOPILOT_CONNECT: cad.id });

  await expect
    .poll(async () => (await getSnapshot(started.page)).connection, { timeout: 20_000 })
    .toMatchObject({ status: 'connected', connector: { id: cad.id }, docked: true });
  expect(cad.setBoundsCalls).toHaveLength(1);
  expect(cad.setBoundsCalls[0]).not.toEqual(cad.initialBounds);

  // Docking must not have changed the stored panel width.
  await started.page.waitForTimeout(600);
  expect((await getSnapshot(started.page)).settings.panelWidth).toBe(PANEL_WIDTH_DEFAULT);

  expect(await quitThroughWindowClose(started)).toBe(true);
  launched = undefined;
  expect(cad.setBoundsCalls.at(-1)).toEqual(cad.initialBounds);
  await expect.poll(() => cad.clientCount()).toBe(0);
});

test('a second launch focuses the running copilot and hands over its connect request', async () => {
  presetSettings({ autoConnect: false, autoDock: false });
  const cad = await startFakeCad(homeDir);
  fakeCad = cad;
  const { app, page } = await start();
  expect((await getSnapshot(page)).connection).toEqual({ status: 'disconnected' });
  // Hidden mode never minimizes or restores the window (that would put it on the screen).
  if (!HIDDEN_WINDOWS) {
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.minimize());
    await expect.poll(() => app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.isMinimized())).toBe(true);
  }

  // The second instance exits on its own.
  const exitCode = await runSecondInstance({ userDataDir, homeDir, env: { CADCOPILOT_CONNECT: cad.id } });
  expect(exitCode).toBe(0);

  await expect
    .poll(async () => (await getSnapshot(page)).connection, { timeout: 15_000 })
    .toMatchObject({ status: 'connected', connector: { id: cad.id }, docked: false });
  expect(await windowCount(app)).toBe(1);
  expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.isMinimized())).toBe(false);
  expect(cad.clientCount()).toBe(1);
});
