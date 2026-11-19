/**
 * End-to-end tests of the built CAD Copilot app against a fake CAD with an in-memory drawing
 * (fake-drawing.ts), driven only through the UI with real clicks and typing. The scripted test
 * model replaces Claude (CADCOPILOT_E2E=1 + CADCOPILOT_SCRIPT); nothing touches the network.
 * Screenshots go to test-results/screens/.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type ElectronApplication, type Page } from '@playwright/test';
import type { WindowBounds } from '@cadstudio/cad-protocol';
import { WINDOWS_INVISIBLE_FRAME } from '../src/main/docking';
import { PANEL_WIDTH_DEFAULT } from '../src/shared/bridge';
import { STALE_REVISION_MESSAGE, startFakeDrawingCad, type FakeDrawingCad } from './fake-drawing';
import { appDir, closeApp, createTempDir, getSnapshot, launchCopilot, removeTempDir, windowInfo, type LaunchedApp } from './helpers';

const SCREENS_DIR = path.join(appDir, 'test-results', 'screens');
/** Pixel tolerance for window geometry (DPI rounding). */
const TOLERANCE = 2;

let launched: LaunchedApp | undefined;
const fakes: FakeDrawingCad[] = [];
let userDataDir: string;
let homeDir: string;

interface ScriptTurn {
  text?: string;
  toolCalls?: { name: string; input?: unknown }[];
  delayMs?: number;
}

function writeScript(turns: ScriptTurn[]): string {
  const file = path.join(homeDir, 'script.json');
  writeFileSync(file, JSON.stringify({ turns }));
  return file;
}

function presetSettings(settings: Record<string, unknown>): void {
  mkdirSync(userDataDir, { recursive: true });
  writeFileSync(path.join(userDataDir, 'settings.json'), JSON.stringify({ version: 1, ...settings }));
}

async function startFake(options: Partial<Parameters<typeof startFakeDrawingCad>[0]> = {}): Promise<FakeDrawingCad> {
  const fake = await startFakeDrawingCad({ homeDir, ...options });
  fakes.push(fake);
  return fake;
}

async function start(env: Record<string, string> = {}): Promise<LaunchedApp> {
  launched = await launchCopilot({ userDataDir, homeDir, env });
  await expect(launched.page.getByTestId('copilot-root')).toBeVisible();
  return launched;
}

async function screenshot(page: Page, name: string): Promise<void> {
  mkdirSync(SCREENS_DIR, { recursive: true });
  // Let transitions and fonts settle.
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(SCREENS_DIR, `${name}.png`) });
}

async function setWindowWidth(app: ElectronApplication, width: number): Promise<void> {
  await app.evaluate(({ BrowserWindow }, w) => {
    const win = BrowserWindow.getAllWindows()[0];
    if (win === undefined) throw new Error('no window');
    const bounds = win.getBounds();
    win.setBounds({ ...bounds, width: w, height: Math.max(bounds.height, 760) });
  }, width);
}

/** Opens the connection popover from the chip and connects to a fake by clicking it. */
async function connectByClicking(page: Page, fake: FakeDrawingCad): Promise<void> {
  await page.getByTestId('connection-chip').click();
  const popover = page.getByTestId('connection-popover');
  await expect(popover).toBeVisible();
  await popover.getByTestId(`connector-connect-${fake.id}`).click();
  await expect(page.getByTestId('connection-chip')).toHaveAttribute('data-status', 'connected');
  await page.keyboard.press('Escape');
  await expect(popover).toBeHidden();
}

async function send(page: Page, text: string): Promise<void> {
  const input = page.getByTestId('composer-input');
  await input.click();
  await input.fill('');
  await input.pressSequentially(text, { delay: 5 });
  await page.getByTestId('composer-send').click();
  await expect(page.getByTestId('message-user').last()).toContainText(text);
}

async function waitIdle(page: Page): Promise<void> {
  await expect(page.getByTestId('composer-send')).toBeVisible({ timeout: 30_000 });
  await expect.poll(async () => (await getSnapshot(page)).busy).toBe(false);
}

/** A physical-pixel rectangle converted to DIP and the work area of its display, from the app. */
async function dipAndWorkArea(app: ElectronApplication, rect: WindowBounds): Promise<{ dip: WindowBounds; workArea: WindowBounds }> {
  return app.evaluate(({ screen }, physical) => {
    const dip = typeof screen.screenToDipRect === 'function' ? screen.screenToDipRect(null, physical) : physical;
    return { dip, workArea: screen.getDisplayMatching(dip).workArea };
  }, rect);
}

test.beforeEach(() => {
  userDataDir = createTempDir('fake-ud');
  homeDir = createTempDir('fake-home');
});

test.afterEach(async () => {
  await closeApp(launched?.app);
  launched = undefined;
  for (const fake of fakes.splice(0)) await fake.close().catch(() => undefined);
  removeTempDir(userDataDir);
  removeTempDir(homeDir);
});

test('a. lists and connects to the fake CAD by clicking, docks beside it and undocks', async () => {
  presetSettings({ autoConnect: false, autoDock: false });
  const fake = await startFake();
  const { app, page } = await start({ CADCOPILOT_SCRIPT: writeScript([]) });

  const chip = page.getByTestId('connection-chip');
  await expect(chip).toHaveAttribute('data-status', 'disconnected');
  await expect(chip).toContainText('Not connected');
  // The empty state offers the running CAD.
  await expect(page.getByTestId('connect-card')).toBeVisible();
  await expect(page.getByTestId('connect-card')).toContainText('Fake CAD', { timeout: 10_000 });
  await screenshot(page, 'a1-not-connected');

  await connectByClicking(page, fake);
  await expect(chip).toContainText('Fake CAD');
  await expect(chip).toContainText('Ground Floor.cadstudio');
  expect(fake.clientCount()).toBe(1);
  await expect(page.getByTestId('welcome-card')).toBeVisible();
  // The drawing is in millimetres: the example prompts use metric sizes.
  await expect(page.getByTestId('example-prompt').first()).toHaveText('Draw a 4 x 5 m room');
  await screenshot(page, 'a2-connected-welcome');

  // Dock: the CAD takes the left part of the work area, the copilot the right column.
  await chip.click();
  await page.getByTestId('dock-toggle').click();
  await expect(page.getByTestId('dock-toggle')).toHaveAttribute('data-docked', 'true');
  expect(fake.setBoundsCalls).toHaveLength(1);
  const { dip: cadDocked, workArea } = await dipAndWorkArea(app, fake.setBoundsCalls[0] as WindowBounds);
  const panel = (await windowInfo(app)).bounds;
  // On Windows the CAD's outer bounds include its invisible border (left, right, bottom).
  const inset = process.platform === 'win32' ? WINDOWS_INVISIBLE_FRAME : 0;
  expect(Math.abs(cadDocked.x + inset - workArea.x)).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(cadDocked.y - workArea.y)).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(cadDocked.height - inset - workArea.height)).toBeLessThanOrEqual(TOLERANCE);
  expect(cadDocked.width).toBeGreaterThanOrEqual(400);
  expect(cadDocked.width).toBeLessThan(workArea.width);
  expect(Math.abs(panel.x - (cadDocked.x + cadDocked.width - inset))).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(panel.x + panel.width - (workArea.x + workArea.width))).toBeLessThanOrEqual(TOLERANCE);
  expect(Math.abs(panel.height - workArea.height)).toBeLessThanOrEqual(TOLERANCE);
  await screenshot(page, 'a3-docked-popover');

  // Undock: the CAD window gets its earlier bounds back.
  await page.getByTestId('dock-toggle').click();
  await expect(page.getByTestId('dock-toggle')).toHaveAttribute('data-docked', 'false');
  expect(fake.setBoundsCalls).toHaveLength(2);
  expect(fake.setBoundsCalls[1]).toEqual(fake.initialBounds);
  expect(fake.bounds()).toEqual(fake.initialBounds);
  expect((await windowInfo(app)).bounds.width).toBe(PANEL_WIDTH_DEFAULT);
});

test('b. a scripted conversation: questions, plan, CAD activity and closing text', async () => {
  presetSettings({ autoConnect: false, autoDock: false });
  const fake = await startFake();
  const script = writeScript([
    {
      text: 'Before I draw, two quick questions.',
      toolCalls: [
        {
          name: 'ask_user',
          input: {
            questions: [
              {
                header: 'Room',
                question: 'Which room should I draw?',
                options: [
                  { label: 'Bedroom', description: 'A 4 x 5 m bedroom', recommended: true },
                  { label: 'Office', description: 'A 3 x 4 m office' },
                ],
              },
              {
                header: 'Layer',
                question: 'Which layer should the walls go on?',
                options: [{ label: 'A new "Walls" layer' }, { label: 'Layer 0' }],
              },
            ],
          },
        },
      ],
    },
    {
      toolCalls: [
        {
          name: 'update_plan',
          input: {
            tasks: [
              { id: 't1', title: 'Check the drawing', status: 'in_progress' },
              { id: 't2', title: 'Create the Walls layer', status: 'pending' },
              { id: 't3', title: 'Draw the bedroom outline', status: 'pending' },
            ],
          },
        },
        { name: 'get_drawing_info', input: {} },
      ],
    },
    {
      toolCalls: [
        { name: 'create_layer', input: { name: 'Walls', color: '#ff8800' } },
        {
          name: 'create_entities',
          input: { layer: 'Walls', entities: [{ type: 'rectangle', corner: { x: 0, y: 0 }, width: 4000, height: 5000 }] },
        },
        { name: 'zoom_extents', input: {} },
      ],
    },
    {
      toolCalls: [
        {
          name: 'update_plan',
          input: {
            tasks: [
              { id: 't1', title: 'Check the drawing', status: 'done' },
              { id: 't2', title: 'Create the Walls layer', status: 'done' },
              { id: 't3', title: 'Draw the bedroom outline', status: 'done' },
            ],
          },
        },
      ],
    },
    { text: 'Done: the **4 x 5 m bedroom** outline is on the new *Walls* layer.' },
  ]);
  const { page } = await start({ CADCOPILOT_SCRIPT: script });
  await connectByClicking(page, fake);

  await send(page, 'Draw a bedroom');

  // The question card: pick the recommended option for Q1 and the second option for Q2.
  const card = page.getByTestId('question-card');
  await expect(card).toHaveAttribute('data-state', 'pending');
  await expect(card).toContainText('(Recommended)');
  await expect(card).toContainText('ROOM');
  await screenshot(page, 'b1-question-card');
  await expect(page.getByTestId('question-submit')).toBeDisabled();
  await page.getByTestId('question-option-0-0').click();
  await page.getByTestId('question-option-1-1').click();
  await page.getByTestId('question-submit').click();

  await expect(page.getByTestId('message-assistant').last()).toContainText('4 x 5 m bedroom', { timeout: 30_000 });
  await waitIdle(page);

  // The answered card collapses to the chosen answers with check marks.
  await expect(card).toHaveAttribute('data-state', 'answered');
  await expect(card).toContainText('Bedroom');
  await expect(card).toContainText('Layer 0');
  await expect(card.locator('svg.lucide-check')).toHaveCount(2);
  await expect(card.getByTestId('question-option-0-0')).toHaveCount(0);

  // CAD activity: four rows, all done with check marks.
  const rows = page.getByTestId('activity-row');
  await expect(rows).toHaveCount(4);
  for (let index = 0; index < 4; index += 1) {
    await expect(rows.nth(index)).toHaveAttribute('data-status', 'done');
    await expect(rows.nth(index).locator('svg.lucide-check')).toHaveCount(1);
  }
  expect(await rows.evaluateAll((list) => list.map((row) => row.getAttribute('data-tool')))).toEqual([
    'get_drawing_info',
    'create_layer',
    'create_entities',
    'zoom_extents',
  ]);
  await expect(page.getByTestId('task-progress').last()).toContainText('Task Progress (3/3)');
  await expect(page.getByTestId('message-assistant').last().locator('strong')).toHaveText('4 x 5 m bedroom');
  await screenshot(page, 'b2-conversation-done');

  // The fake received exactly those calls, in order, from the AI, with the user's instruction.
  expect(fake.toolCalls.map((call) => call.name)).toEqual(['get_drawing_info', 'create_layer', 'create_entities', 'zoom_extents']);
  for (const call of fake.toolCalls) {
    expect(call.source).toBe('ai');
    expect(call.instruction).toBe('Draw a bedroom');
  }
  expect(fake.toolCalls[1]?.expectedRevision).toBe(1);
  expect(fake.toolCalls[2]?.expectedRevision).toBe(2);
  expect(fake.layerNames()).toEqual(['0', 'Walls']);
  expect(fake.entityIds()).toEqual(['e1']);
  // The chip follows the drawing.
  await expect.poll(async () => {
    const { connection } = await getSnapshot(page);
    return connection.status === 'connected' ? connection.document?.revision : null;
  }).toBe(3);

  // Layout at the three supported widths: no horizontal overflow anywhere.
  for (const width of [380, 460, 760]) {
    await setWindowWidth(launched!.app, width);
    await page.waitForTimeout(200);
    const overflow = await page.evaluate(() => {
      // Runs in the page; this tsconfig has no DOM library, so the DOM is typed minimally here.
      interface Box { right: number }
      interface El { getBoundingClientRect(): Box; getAttribute(name: string): string | null }
      const doc = (globalThis as unknown as {
        document: { documentElement: { clientWidth: number; scrollWidth: number }; querySelectorAll(s: string): ArrayLike<El> };
      }).document;
      const root = doc.documentElement;
      const offenders = Array.from(doc.querySelectorAll('[data-testid]'))
        .filter((el) => el.getBoundingClientRect().right > root.clientWidth + 1)
        .map((el) => el.getAttribute('data-testid'));
      return { scroll: root.scrollWidth - root.clientWidth, offenders };
    });
    expect(overflow).toEqual({ scroll: 0, offenders: [] });
    await screenshot(page, `b3-width-${width}`);
  }
});

test('c+d. approvals (reject, then approve) and a stale drawing revision', async () => {
  presetSettings({ autoConnect: false, autoDock: false });
  const fake = await startFake();
  const rectangle = (x: number) => ({ type: 'rectangle', corner: { x, y: 0 }, width: 1000, height: 1000 });
  const script = writeScript([
    // Turn 1: draw two rectangles.
    { toolCalls: [{ name: 'create_entities', input: { entities: [rectangle(0), rectangle(2000)] } }] },
    { text: 'Two squares are drawn.' },
    // Turn 2: delete one (rejected).
    { toolCalls: [{ name: 'delete_entities', input: { ids: ['e1'] } }] },
    { text: 'Understood, I left it in place.' },
    // Turn 3: delete it again (approved).
    { toolCalls: [{ name: 'delete_entities', input: { ids: ['e1'] } }] },
    { text: 'Deleted.' },
    // Turn 4: the drawing changed meanwhile: the write is refused, then inspect and retry.
    { toolCalls: [{ name: 'create_entities', input: { entities: [rectangle(4000)] } }] },
    { toolCalls: [{ name: 'inspect_entities', input: {} }] },
    { toolCalls: [{ name: 'create_entities', input: { entities: [rectangle(4000)] } }] },
    { text: 'The drawing had changed, so I checked it again and then added the square.' },
  ]);
  const { page } = await start({ CADCOPILOT_SCRIPT: script });
  await connectByClicking(page, fake);

  await send(page, 'Draw two squares');
  await expect(page.getByTestId('message-assistant').last()).toContainText('Two squares are drawn.');
  await waitIdle(page);
  expect(fake.entityIds()).toEqual(['e1', 'e2']);

  // --- Reject ---
  await send(page, 'Delete the first square');
  const approval = page.getByTestId('approval-card').last();
  await expect(approval).toHaveAttribute('data-state', 'pending');
  await screenshot(page, 'c1-approval-pending');
  await approval.getByTestId('approval-reject').click();
  await expect(page.getByTestId('message-assistant').last()).toContainText('I left it in place');
  await waitIdle(page);
  await expect(approval).toHaveAttribute('data-state', 'rejected');
  expect(fake.toolCalls.map((call) => call.name)).toEqual(['create_entities']);
  expect(fake.entityIds()).toEqual(['e1', 'e2']);

  // --- Approve ---
  await send(page, 'Delete the first square after all');
  const second = page.getByTestId('approval-card').last();
  await expect(second).toHaveAttribute('data-state', 'pending');
  await second.getByTestId('approval-approve').click();
  await expect(page.getByTestId('message-assistant').last()).toContainText('Deleted.');
  await waitIdle(page);
  expect(fake.toolCalls.map((call) => call.name)).toEqual(['create_entities', 'delete_entities']);
  expect(fake.entityIds()).toEqual(['e2']);
  await screenshot(page, 'c2-approved');

  // --- Stale revision: the user edits the drawing in the CAD between turns ---
  fake.externalEdit();
  const editedRevision = fake.document().revision;
  await expect.poll(async () => {
    const { connection } = await getSnapshot(page);
    return connection.status === 'connected' ? connection.document?.revision : null;
  }).toBe(editedRevision);
  await send(page, 'Add a third square');
  await expect(page.getByTestId('message-assistant').last()).toContainText('checked it again', { timeout: 30_000 });
  await waitIdle(page);

  const lastCalls = fake.toolCalls.slice(2);
  expect(lastCalls.map((call) => call.name)).toEqual(['create_entities', 'inspect_entities', 'create_entities']);
  expect(lastCalls[0]?.expectedRevision).toBeLessThan(editedRevision);
  expect(lastCalls[2]?.expectedRevision).toBe(editedRevision);
  expect(fake.entityIds()).toEqual(['e2', 'e3', 'e4']);

  const failed = page.locator('[data-testid="activity-row"][data-status="failed"]');
  await expect(failed).toHaveCount(1);
  await expect(failed).toContainText(STALE_REVISION_MESSAGE);
  await expect(failed.locator('svg.lucide-x')).toHaveCount(1);
  const lastRows = page.getByTestId('activity-row');
  await expect(lastRows.last()).toHaveAttribute('data-status', 'done');
  await screenshot(page, 'd1-stale-revision');
});

test('e. Stop during a long turn shows Stopped and the composer works again', async () => {
  presetSettings({ autoConnect: false, autoDock: false });
  const fake = await startFake();
  const script = writeScript([
    { text: 'Let me think about this floor plan for a long while before doing anything at all.', delayMs: 30_000 },
    { text: 'Here I am again.' },
  ]);
  const { page } = await start({ CADCOPILOT_SCRIPT: script });
  await connectByClicking(page, fake);

  await send(page, 'Plan a whole house');
  const stop = page.getByTestId('composer-stop');
  await expect(stop).toBeVisible();
  await page.waitForTimeout(1200);
  await screenshot(page, 'e1-busy');
  await stop.click();
  await expect(page.getByTestId('turn-footer').last()).toHaveAttribute('data-stopped', 'true');
  await expect(page.getByTestId('turn-footer').last()).toContainText('Stopped');
  // The scripted model answered (nobody else): no Claude model name, and no empty token count.
  await expect(page.getByTestId('turn-footer').last()).not.toContainText('Claude');
  await expect(page.getByTestId('turn-footer').last()).not.toContainText(' in / ');
  await waitIdle(page);
  await screenshot(page, 'e2-stopped');

  await send(page, 'Are you there?');
  await expect(page.getByTestId('message-assistant').last()).toContainText('Here I am again.');
  await waitIdle(page);
  expect(fake.toolCalls).toHaveLength(0);
});

test('f. the CAD closing shows Not connected; when it starts again the copilot reconnects', async () => {
  presetSettings({ autoDock: false });
  const first = await startFake();
  const { page } = await start({ CADCOPILOT_SCRIPT: writeScript([]) });
  const chip = page.getByTestId('connection-chip');

  // Auto-connect: exactly one CAD is running.
  await expect(chip).toHaveAttribute('data-status', 'connected', { timeout: 15_000 });
  await first.close();
  await expect(chip).toHaveAttribute('data-status', 'disconnected');
  await expect(chip).toContainText('Not connected');
  await screenshot(page, 'f1-cad-closed');

  // Let one discovery poll see the CAD gone before it comes back.
  await page.waitForTimeout(3_000);
  const again = await startFake({ documentName: 'Reopened.cadstudio' });
  await expect(chip).toHaveAttribute('data-status', 'connected', { timeout: 15_000 });
  await expect(chip).toContainText('Reopened.cadstudio');
  expect(again.clientCount()).toBe(1);
});

test('g1. a corrupt settings.json falls back to the defaults and is repaired', async () => {
  mkdirSync(userDataDir, { recursive: true });
  const settingsFile = path.join(userDataDir, 'settings.json');
  writeFileSync(settingsFile, '{"version": 1, "model": ');
  const { page } = await start({ CADCOPILOT_SCRIPT: writeScript([]) });
  await expect(page.getByTestId('composer-input')).toBeVisible();
  const { settings } = await getSnapshot(page);
  expect(settings.model).toBe('claude-opus-5-5');
  expect(settings.panelWidth).toBe(PANEL_WIDTH_DEFAULT);

  await page.getByTestId('effort-select').selectOption('low');
  await expect.poll(() => {
    try {
      return (JSON.parse(readFileSync(settingsFile, 'utf8')) as { effort?: string }).effort;
    } catch {
      return 'unreadable';
    }
  }).toBe('low');
});

test('g2. an advert of a dead process is ignored and removed', async () => {
  // A pid that existed a moment ago and is now gone.
  const probe = spawnSync(process.execPath, ['-e', 'process.stdout.write(String(process.pid))'], { encoding: 'utf8' });
  const deadPid = Number(probe.stdout);
  expect(deadPid).toBeGreaterThan(0);
  const connectors = path.join(homeDir, 'connectors');
  mkdirSync(connectors, { recursive: true });
  const advertFile = path.join(connectors, `cadstudio-${deadPid}.json`);
  writeFileSync(
    advertFile,
    JSON.stringify({
      id: `cadstudio-${deadPid}`,
      cad: 'cadstudio',
      displayName: 'Crashed CAD',
      cadVersion: '1.0.0',
      connectorVersion: '1.0.0',
      protocolVersion: 1,
      pipeName: `\\\\.\\pipe\\cadcopilot-cadstudio-${deadPid}-dead`,
      token: 'a'.repeat(64),
      pid: deadPid,
      startedAt: new Date().toISOString(),
    }),
  );

  const { page } = await start({ CADCOPILOT_SCRIPT: writeScript([]) });
  await expect.poll(() => existsSync(advertFile), { timeout: 10_000 }).toBe(false);
  await expect(page.getByTestId('connection-chip')).toHaveAttribute('data-status', 'disconnected');
  await expect(page.getByTestId('connect-card')).toBeVisible();
  await expect(page.getByTestId('connect-card')).not.toContainText('Crashed CAD');
  expect((await getSnapshot(page)).connectors).toEqual([]);
  expect(readdirSync(connectors)).toEqual([]);
});

test('g3. two CADs at once: no auto-connect, the user picks one', async () => {
  const studio = await startFake();
  const acad = await startFake({ cad: 'autocad', displayName: 'AutoCAD 2026', documentName: 'Site.dwg' });
  const { page } = await start({ CADCOPILOT_SCRIPT: writeScript([]) });

  const chip = page.getByTestId('connection-chip');
  await expect(page.getByTestId('connect-card')).toContainText('AutoCAD 2026', { timeout: 10_000 });
  await expect(page.getByTestId('connect-card')).toContainText('Fake CAD');
  await page.waitForTimeout(2_500);
  await expect(chip).toHaveAttribute('data-status', 'disconnected');
  await screenshot(page, 'g3-two-cads');

  await chip.click();
  await expect(page.getByTestId(`connector-item-${studio.id}`)).toBeVisible();
  await page.getByTestId(`connector-connect-${acad.id}`).click();
  await expect(chip).toHaveAttribute('data-status', 'connected');
  await expect(chip).toContainText('AutoCAD 2026');
  await expect(chip).toContainText('Site.dwg');
  expect(acad.clientCount()).toBe(1);
  expect(studio.clientCount()).toBe(0);
  // Autodock is on by default: the chosen CAD was docked, the other one left alone.
  await expect.poll(() => acad.setBoundsCalls.length).toBe(1);
  expect(studio.setBoundsCalls).toHaveLength(0);
});

test('g4. no CAD at all, and no API key without the test model', async () => {
  const withModel = await start({ CADCOPILOT_SCRIPT: writeScript([{ text: 'I can answer, but I cannot draw without a CAD.' }]) });
  const { page } = withModel;
  await expect(page.getByTestId('connection-chip')).toHaveAttribute('data-status', 'disconnected');
  await expect(page.getByTestId('connect-card')).toBeVisible();
  await expect(page.getByTestId('composer-hint')).toContainText('No CAD application is connected');
  await screenshot(page, 'g4-no-cad');
  await send(page, 'Hello');
  await expect(page.getByTestId('message-assistant').last()).toContainText('cannot draw');
  await waitIdle(page);
  await closeApp(withModel.app);
  launched = undefined;

  // Without the test model and without a key, the setup card asks for one.
  const plain = await start();
  await expect(plain.page.getByTestId('api-key-card')).toBeVisible();
  await expect(plain.page.getByTestId('composer-send')).toBeDisabled();
  await screenshot(plain.page, 'g4-no-api-key');
  for (const width of [380, 760]) {
    await setWindowWidth(plain.app, width);
    await screenshot(plain.page, `g4-no-api-key-${width}`);
  }
});
