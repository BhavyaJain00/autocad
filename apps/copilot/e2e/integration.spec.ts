/**
 * Joint end-to-end tests: the built CAD Copilot app drives the REAL built CAD Studio app.
 *
 * Both apps run as separate Electron processes that share one temporary CADCOPILOT_HOME (the
 * connector advert folder), so they find each other exactly as they do on a user's machine, over
 * CAD Studio's real named pipe. The copilot uses the scripted test model (CADCOPILOT_E2E=1 +
 * CADCOPILOT_SCRIPT); nothing touches the network and no live Claude result is involved. The
 * copilot is driven only through its UI with real clicks and typing; CAD Studio's drawing is read
 * back through its e2e hook `window.__cadstudioTest.editor`, and user edits in CAD Studio are made
 * with real keyboard and mouse input. Screenshots go to test-results/screens/joint-*.png.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import {
  captureScreen,
  childAppProcesses,
  closeStudio,
  killProcessTree,
  killStudio,
  launchStudio,
  studioDocument,
  studioHistory,
  windowBounds,
  workAreaOf,
  type Bounds,
  type LaunchedStudio,
  type StudioDocument,
} from './cadstudio';
import { WINDOWS_INVISIBLE_FRAME } from '../src/main/docking';
import { appDir, closeApp, createTempDir, getSnapshot, launchCopilot, removeTempDir, type LaunchedApp } from './helpers';

const SCREENS_DIR = path.join(appDir, 'test-results', 'screens');
/** One foot in millimetres. */
const FT = 304.8;
/** Window geometry tolerance in DIP (rounding of physical <-> DIP conversions). */
const TOLERANCE = 3;
/** Invisible border of CAD Studio's framed window that docking compensates (Windows only). */
const INSET = process.platform === 'win32' ? WINDOWS_INVISIBLE_FRAME : 0;

let userDataDir: string;
let homeDir: string;
let copilot: LaunchedApp | undefined;
const studios: LaunchedStudio[] = [];
/** Pids of copilot processes CAD Studio started (test g): killed after each test. */
const extraPids: number[] = [];

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

async function startStudio(env: Record<string, string> = {}): Promise<LaunchedStudio> {
  const studio = await launchStudio({ homeDir, env });
  studios.push(studio);
  return studio;
}

async function startCopilot(script: ScriptTurn[]): Promise<LaunchedApp> {
  copilot = await launchCopilot({ userDataDir, homeDir, env: { CADCOPILOT_SCRIPT: writeScript(script) } });
  await expect(copilot.page.getByTestId('copilot-root')).toBeVisible();
  return copilot;
}

async function shot(page: Page, name: string): Promise<void> {
  mkdirSync(SCREENS_DIR, { recursive: true });
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(SCREENS_DIR, `joint-${name}.png`) });
}

/** Screenshots of both windows: joint-<name>-studio.png and joint-<name>-copilot.png. */
async function shotBoth(studio: LaunchedStudio, name: string): Promise<void> {
  await shot(studio.page, `${name}-studio`);
  if (copilot !== undefined) await shot(copilot.page, `${name}-copilot`);
}

/** Opens the connection popover from the chip and connects to CAD Studio by clicking it. */
async function connectByClicking(page: Page, studio: LaunchedStudio): Promise<void> {
  await page.getByTestId('connection-chip').click();
  const popover = page.getByTestId('connection-popover');
  await expect(popover).toBeVisible();
  await popover.getByTestId(`connector-connect-${studio.advertId}`).click({ timeout: 20_000 });
  await expect(page.getByTestId('connection-chip')).toHaveAttribute('data-status', 'connected');
  await page.keyboard.press('Escape');
  await expect(popover).toBeHidden();
}

async function expectConnectedTo(page: Page, studio: LaunchedStudio): Promise<void> {
  await expect(page.getByTestId('connection-chip')).toHaveAttribute('data-status', 'connected', { timeout: 20_000 });
  await expect.poll(async () => {
    const { connection } = await getSnapshot(page);
    return connection.status === 'connected' ? connection.connector.id : null;
  }).toBe(studio.advertId);
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

function layerIdByName(document: StudioDocument, name: string): string | undefined {
  return document.layers.find((layer) => layer.name === name)?.id;
}

function expectClose(actual: number, expected: number, what: string): void {
  expect(Math.abs(actual - expected), `${what}: ${actual} vs ${expected}`).toBeLessThanOrEqual(TOLERANCE);
}

/** Types a CAD Studio command-line entry and confirms it with Enter (real keyboard input). */
async function studioEnter(studio: LaunchedStudio, text: string): Promise<void> {
  if (text !== '') await studio.page.keyboard.type(text);
  await studio.page.keyboard.press('Enter');
}

/** Draws a line by hand in CAD Studio: LINE, two clicks on the canvas, Escape. */
async function drawLineByHand(studio: LaunchedStudio): Promise<void> {
  const { page } = studio;
  const box = await page.getByTestId('canvas-host').boundingBox();
  if (box === null) throw new Error('CAD Studio has no visible canvas.');
  await studioEnter(studio, 'L');
  const a = { x: box.x + box.width * 0.3, y: box.y + box.height * 0.7 };
  const b = { x: box.x + box.width * 0.6, y: box.y + box.height * 0.75 };
  for (const p of [a, b]) {
    await page.mouse.move(p.x - 5, p.y - 5);
    await page.mouse.move(p.x, p.y, { steps: 3 });
    await page.mouse.down();
    await page.mouse.up();
  }
  await page.keyboard.press('Escape');
  await page.keyboard.press('Escape');
}

test.beforeEach(() => {
  userDataDir = createTempDir('joint-ud');
  homeDir = createTempDir('joint-home');
});

test.afterEach(async () => {
  await closeApp(copilot?.app);
  copilot = undefined;
  for (const pid of extraPids.splice(0)) killProcessTree(pid);
  for (const studio of studios.splice(0)) await closeStudio(studio.app);
  removeTempDir(userDataDir);
  removeTempDir(homeDir);
});

test('a. the copilot lists CAD Studio, connects, docks beside the real window and undocks', async () => {
  presetSettings({ autoConnect: false, autoDock: false });
  const studio = await startStudio();
  const { app, page } = await startCopilot([]);
  const drawing = await studioDocument(studio.page);

  const chip = page.getByTestId('connection-chip');
  await expect(chip).toHaveAttribute('data-status', 'disconnected');
  await expect(page.getByTestId('connect-card')).toContainText('CAD Studio', { timeout: 15_000 });
  await connectByClicking(page, studio);
  await expectConnectedTo(page, studio);
  await expect(chip).toContainText('CAD Studio');
  await expect(chip).toContainText(drawing.name);
  await expect(page.getByTestId('welcome-card')).toBeVisible();
  // CAD Studio's status bar shows the connection too.
  await expect(studio.page.getByTestId('status-copilot')).toHaveAttribute('data-connected', 'true');
  await shotBoth(studio, 'a1-connected');

  // Dock with the real windows: CAD Studio on the left of the work area, the copilot on the right.
  const before = await windowBounds(studio.app);
  await chip.click();
  await page.getByTestId('dock-toggle').click();
  await expect(page.getByTestId('dock-toggle')).toHaveAttribute('data-docked', 'true');
  const workArea = await workAreaOf(studio.app);
  await expect.poll(async () => (await windowBounds(studio.app)).width, { timeout: 10_000 }).toBeLessThan(workArea.width - 300);
  const cad: Bounds = await windowBounds(studio.app);
  const panel: Bounds = await windowBounds(app);
  // CAD Studio's outer bounds include Windows' invisible border (left, right, bottom): its
  // VISIBLE frame fills the left part of the work area and meets the copilot without a gap.
  expectClose(cad.x + INSET, workArea.x, 'CAD Studio visible left edge');
  expectClose(cad.y, workArea.y, 'CAD Studio y');
  expectClose(cad.height - INSET, workArea.height, 'CAD Studio visible height');
  expectClose(panel.x, cad.x + cad.width - INSET, 'copilot x (visible right edge of CAD Studio)');
  expectClose(panel.x + panel.width, workArea.x + workArea.width, 'copilot right edge');
  expectClose(panel.y, workArea.y, 'copilot y');
  expectClose(panel.height, workArea.height, 'copilot height');
  expect(cad.width).toBeGreaterThanOrEqual(400);
  await page.keyboard.press('Escape');
  await shotBoth(studio, 'a2-docked');
  // The real screen, to see the two windows side by side (frames and the seam between them).
  await page.waitForTimeout(300);
  captureScreen(path.join(SCREENS_DIR, 'joint-a2-docked-screen.png'));

  // Undock: CAD Studio gets its previous bounds back.
  await chip.click();
  await page.getByTestId('dock-toggle').click();
  await expect(page.getByTestId('dock-toggle')).toHaveAttribute('data-docked', 'false');
  await expect.poll(async () => {
    const after = await windowBounds(studio.app);
    return [after.x - before.x, after.y - before.y, after.width - before.width, after.height - before.height].every(
      (delta) => Math.abs(delta) <= TOLERANCE,
    );
  }, { timeout: 10_000 }).toBe(true);
  await page.keyboard.press('Escape');
});

test('b. a scripted conversation draws a 12 x 14 ft room with a table in CAD Studio, undone with one Ctrl+Z', async () => {
  presetSettings({ autoConnect: false, autoDock: false });
  const studio = await startStudio();
  const instruction = 'Draw a 12 x 14 ft room with a round table';
  const { page } = await startCopilot([
    {
      text: 'Two quick questions first.',
      toolCalls: [
        {
          name: 'ask_user',
          input: {
            questions: [
              {
                header: 'Layer',
                question: 'Which layer should the room go on?',
                options: [{ label: 'A new "AI Room" layer', recommended: true }, { label: 'The current layer' }],
              },
              {
                header: 'Table',
                question: 'How big should the table be?',
                options: [{ label: '4 ft round' }, { label: '3 ft round' }],
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
              { id: 't2', title: 'Create the AI Room layer', status: 'pending' },
              { id: 't3', title: 'Draw the room and the table', status: 'pending' },
            ],
          },
        },
        { name: 'get_drawing_info', input: {} },
      ],
    },
    {
      toolCalls: [
        { name: 'create_layer', input: { name: 'AI Room', color: '#ff8800' } },
        {
          name: 'create_entities',
          input: {
            layer: 'AI Room',
            unit: 'ft',
            entities: [
              { type: 'rectangle', corner: { x: 0, y: 0 }, width: 12, height: 14 },
              { type: 'circle', center: { x: 6, y: 7 }, radius: 2 },
            ],
          },
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
              { id: 't2', title: 'Create the AI Room layer', status: 'done' },
              { id: 't3', title: 'Draw the room and the table', status: 'done' },
            ],
          },
        },
      ],
    },
    { text: 'Done: the **12 x 14 ft room** with a 4 ft round table is on the new *AI Room* layer.' },
  ]);
  await connectByClicking(page, studio);

  await send(page, instruction);
  const card = page.getByTestId('question-card');
  await expect(card).toHaveAttribute('data-state', 'pending');
  await shotBoth(studio, 'b1-question');
  await page.getByTestId('question-option-0-0').click();
  await page.getByTestId('question-option-1-0').click();
  await page.getByTestId('question-submit').click();

  await expect(page.getByTestId('message-assistant').last()).toContainText('12 x 14 ft room', { timeout: 30_000 });
  await waitIdle(page);

  // The copilot UI: the answered card, four CAD activity rows with check marks, Task Progress 3/3.
  await expect(card).toHaveAttribute('data-state', 'answered');
  await expect(card.locator('svg.lucide-check')).toHaveCount(2);
  const rows = page.getByTestId('activity-row');
  await expect(rows).toHaveCount(4);
  expect(await rows.evaluateAll((list) => list.map((row) => row.getAttribute('data-tool')))).toEqual([
    'get_drawing_info',
    'create_layer',
    'create_entities',
    'zoom_extents',
  ]);
  for (let index = 0; index < 4; index += 1) {
    await expect(rows.nth(index)).toHaveAttribute('data-status', 'done');
    await expect(rows.nth(index).locator('svg.lucide-check')).toHaveCount(1);
  }
  await expect(page.getByTestId('task-progress').last()).toContainText('Task Progress (3/3)');
  await expect(page.getByTestId('message-assistant').last().locator('strong')).toHaveText('12 x 14 ft room');

  // CAD Studio: exact millimetre geometry on the new layer.
  const drawing = await studioDocument(studio.page);
  const layerId = layerIdByName(drawing, 'AI Room');
  expect(layerId).toBeDefined();
  const onLayer = drawing.entities.filter((entity) => entity.layerId === layerId);
  expect(onLayer.map((entity) => entity.type).sort()).toEqual(['circle', 'rectangle']);
  const rectangle = onLayer.find((entity) => entity.type === 'rectangle')!;
  const circle = onLayer.find((entity) => entity.type === 'circle')!;
  expect(rectangle.origin).toEqual({ x: 0, y: 0 });
  expect(rectangle.width).toBeCloseTo(12 * FT, 6);
  expect(rectangle.height).toBeCloseTo(14 * FT, 6);
  expect(rectangle.width).toBeCloseTo(3657.6, 6);
  expect(rectangle.height).toBeCloseTo(4267.2, 6);
  expect(circle.center!.x).toBeCloseTo(6 * FT, 6);
  expect(circle.center!.y).toBeCloseTo(7 * FT, 6);
  expect(circle.radius).toBeCloseTo(2 * FT, 6);

  // The operation history: the two AI operations, each carrying the user's instruction.
  const history = await studioHistory(studio.page);
  const aiOperations = history.filter((operation) => operation.source === 'ai');
  expect(aiOperations).toHaveLength(2);
  for (const operation of aiOperations) expect(operation.instruction).toBe(instruction);
  expect([...aiOperations[1]!.targetIds].sort()).toEqual(onLayer.map((entity) => entity.id).sort());
  await shotBoth(studio, 'b2-room-drawn');

  // ONE Ctrl+Z in CAD Studio removes the whole batch (the room and the table), not the layer.
  await studio.page.keyboard.press('Control+z');
  await expect.poll(async () => (await studioDocument(studio.page)).entities.length).toBe(0);
  const afterUndo = await studioDocument(studio.page);
  expect(layerIdByName(afterUndo, 'AI Room')).toBe(layerId);
  expect((await studioHistory(studio.page)).filter((operation) => operation.source === 'ai')).toHaveLength(1);
  await shotBoth(studio, 'b3-after-undo');
});

test('c. a delete needs approval: Reject leaves the drawing alone, a later Approve deletes', async () => {
  presetSettings({ autoConnect: true, autoDock: false });
  const studio = await startStudio();
  // Two squares drawn by hand in CAD Studio (typed on its command line).
  for (const corner of ['0,0', "6',0"]) {
    await studioEnter(studio, 'REC');
    await studioEnter(studio, corner);
    await studioEnter(studio, "@3',3'");
  }
  await expect.poll(async () => (await studioDocument(studio.page)).entities.length).toBe(2);
  await studio.page.keyboard.press('Escape');
  const [first, second] = (await studioDocument(studio.page)).entities;
  const { page } = await startCopilot([
    { text: 'Deleting the first square.', toolCalls: [{ name: 'delete_entities', input: { ids: [first!.id] } }] },
    { text: 'Understood, I left it in place.' },
    { toolCalls: [{ name: 'delete_entities', input: { ids: [first!.id] } }] },
    { text: 'Deleted.' },
  ]);
  await expectConnectedTo(page, studio);

  await send(page, 'Delete the first square');
  const approval = page.getByTestId('approval-card').last();
  await expect(approval).toHaveAttribute('data-state', 'pending');
  await shotBoth(studio, 'c1-approval-pending');
  await approval.getByTestId('approval-reject').click();
  await expect(page.getByTestId('message-assistant').last()).toContainText('I left it in place');
  await waitIdle(page);
  await expect(approval).toHaveAttribute('data-state', 'rejected');
  expect((await studioDocument(studio.page)).entities.map((entity) => entity.id)).toEqual([first!.id, second!.id]);
  expect((await studioHistory(studio.page)).some((operation) => operation.source === 'ai')).toBe(false);

  await send(page, 'Delete the first square after all');
  const again = page.getByTestId('approval-card').last();
  await expect(again).toHaveAttribute('data-state', 'pending');
  await again.getByTestId('approval-approve').click();
  await expect(page.getByTestId('message-assistant').last()).toContainText('Deleted.');
  await waitIdle(page);
  await expect.poll(async () => (await studioDocument(studio.page)).entities.map((entity) => entity.id)).toEqual([second!.id]);
  const last = (await studioHistory(studio.page)).at(-1);
  expect(last?.source).toBe('ai');
  expect(last?.instruction).toBe('Delete the first square after all');
  expect(last?.targetIds).toEqual([first!.id]);
  await shotBoth(studio, 'c2-approved');
});

test('d. a hand edit in CAD Studio after the model inspected makes its write fail; it re-inspects and succeeds', async () => {
  presetSettings({ autoConnect: true, autoDock: false });
  const studio = await startStudio();
  const square = { type: 'rectangle', corner: { x: 20, y: 0 }, width: 4, height: 4 };
  const { page } = await startCopilot([
    { toolCalls: [{ name: 'get_drawing_info', input: {} }] },
    {
      toolCalls: [
        {
          name: 'ask_user',
          input: { questions: [{ header: 'Size', question: 'Is a 4 ft square right?', options: [{ label: 'Yes' }, { label: 'No' }] }] },
        },
      ],
    },
    { toolCalls: [{ name: 'create_entities', input: { unit: 'ft', entities: [square] } }] },
    { toolCalls: [{ name: 'inspect_entities', input: {} }] },
    { toolCalls: [{ name: 'create_entities', input: { unit: 'ft', entities: [square] } }] },
    { text: 'The drawing had changed, so I checked it again and then added the square.' },
  ]);
  await expectConnectedTo(page, studio);

  await send(page, 'Add a 4 ft square');
  // The model has inspected the drawing and now waits for an answer.
  await expect(page.getByTestId('question-card')).toHaveAttribute('data-state', 'pending');
  await expect(page.getByTestId('activity-row').first()).toHaveAttribute('data-status', 'done');

  // Meanwhile the user draws a line by hand in CAD Studio.
  await drawLineByHand(studio);
  await expect.poll(async () => (await studioDocument(studio.page)).entities.filter((entity) => entity.type === 'line').length).toBe(1);
  await page.getByTestId('question-option-0-0').click();
  await page.getByTestId('question-submit').click();
  await expect(page.getByTestId('message-assistant').last()).toContainText('checked it again', { timeout: 30_000 });
  await waitIdle(page);

  const rows = page.getByTestId('activity-row');
  expect(await rows.evaluateAll((list) => list.map((row) => [row.getAttribute('data-tool'), row.getAttribute('data-status')]))).toEqual([
    ['get_drawing_info', 'done'],
    ['create_entities', 'failed'],
    ['inspect_entities', 'done'],
    ['create_entities', 'done'],
  ]);
  const failed = page.locator('[data-testid="activity-row"][data-status="failed"]');
  await expect(failed).toContainText('The drawing changed since revision');
  // Only the second write reached the drawing: the hand-drawn line plus one square.
  const drawing = await studioDocument(studio.page);
  expect(drawing.entities.map((entity) => entity.type).sort()).toEqual(['line', 'rectangle']);
  const rectangle = drawing.entities.find((entity) => entity.type === 'rectangle')!;
  expect(rectangle.origin!.x).toBeCloseTo(20 * FT, 6);
  expect(rectangle.width).toBeCloseTo(4 * FT, 6);
  await shotBoth(studio, 'd1-stale-refused-then-ok');
});

test('e. Stop during a long scripted turn: nothing reaches CAD Studio and the composer works again', async () => {
  presetSettings({ autoConnect: true, autoDock: false });
  const studio = await startStudio();
  const { page } = await startCopilot([
    {
      text: 'Let me think about this whole floor plan for a long while before I draw anything.',
      delayMs: 30_000,
      toolCalls: [{ name: 'create_entities', input: { unit: 'ft', entities: [{ type: 'circle', center: { x: 0, y: 0 }, radius: 1 }] } }],
    },
    { text: 'Here I am again.' },
  ]);
  await expectConnectedTo(page, studio);

  await send(page, 'Plan a whole house');
  const stop = page.getByTestId('composer-stop');
  await expect(stop).toBeVisible();
  await page.waitForTimeout(1_000);
  await shotBoth(studio, 'e1-busy');
  await stop.click();
  await expect(page.getByTestId('turn-footer').last()).toHaveAttribute('data-stopped', 'true');
  await expect(page.getByTestId('turn-footer').last()).toContainText('Stopped');
  await waitIdle(page);

  await send(page, 'Are you there?');
  await expect(page.getByTestId('message-assistant').last()).toContainText('Here I am again.');
  await waitIdle(page);
  expect((await studioDocument(studio.page)).entities).toHaveLength(0);
  await shotBoth(studio, 'e2-stopped');
});

test('f. closing CAD Studio shows Not connected; a relaunched CAD Studio is reconnected and drivable', async () => {
  presetSettings({ autoConnect: true, autoDock: false });
  const first = await startStudio();
  const { page } = await startCopilot([
    { toolCalls: [{ name: 'get_drawing_info', input: {} }] },
    { text: 'The drawing is empty.' },
  ]);
  await expectConnectedTo(page, first);

  // The user closes CAD Studio's window (a clean drawing closes without a prompt).
  const exited = new Promise<void>((resolve) => first.app.process().once('exit', () => resolve()));
  await first.app.evaluate(({ BrowserWindow }) => {
    const win = BrowserWindow.getAllWindows()[0];
    setTimeout(() => win?.close(), 0);
  });
  await exited;
  const chip = page.getByTestId('connection-chip');
  await expect(chip).toHaveAttribute('data-status', 'disconnected');
  await expect(chip).toContainText('Not connected');
  await shot(page, 'f1-cad-closed-copilot');

  await page.waitForTimeout(2_500);
  const second = await startStudio();
  await expectConnectedTo(page, second);
  await expect(chip).toContainText((await studioDocument(second.page)).name);

  // The new connection works: a tool call reaches the new CAD Studio.
  await send(page, 'What is in the drawing?');
  await expect(page.getByTestId('message-assistant').last()).toContainText('The drawing is empty.');
  await waitIdle(page);
  await expect(page.getByTestId('activity-row').last()).toHaveAttribute('data-status', 'done');
  await shotBoth(second, 'f2-reconnected');
});

test("g. CAD Studio's AI Copilot button starts the copilot, which connects and docks beside it", async () => {
  test.skip(process.platform !== 'win32', 'Finds the started copilot process with Windows process queries');
  presetSettings({ autoConnect: true, autoDock: true });
  const script = writeScript([]);
  // CAD Studio passes its environment on to the copilot it starts: the isolated folders and the
  // scripted test model reach the copilot that way.
  const studio = await startStudio({ CADCOPILOT_E2E: '1', CADCOPILOT_SCRIPT: script, CADCOPILOT_USER_DATA: userDataDir });
  const workArea = await workAreaOf(studio.app);
  await expect(studio.page.getByTestId('status-copilot')).toHaveAttribute('data-connected', 'false');

  await studio.page.getByTestId('tb-copilot').click();
  await expect.poll(() => childAppProcesses(studio.pid, 'copilot').length, { timeout: 20_000 }).toBe(1);
  extraPids.push(...childAppProcesses(studio.pid, 'copilot').map((entry) => entry.pid));
  // No error message in CAD Studio (the IPC answered ok).
  await expect(studio.page.getByTestId('message-text')).toHaveCount(0);

  // The started copilot connects to this CAD Studio and docks beside it.
  await expect(studio.page.getByTestId('status-copilot')).toHaveAttribute('data-connected', 'true', { timeout: 30_000 });
  await expect.poll(async () => {
    const cad = await windowBounds(studio.app);
    return (
      Math.abs(cad.x + INSET - workArea.x) <= TOLERANCE &&
      Math.abs(cad.height - INSET - workArea.height) <= TOLERANCE &&
      cad.width < workArea.width - 300
    );
  }, { timeout: 20_000 }).toBe(true);
  await shot(studio.page, 'g1-started-from-cadstudio-studio');

  // Close it again: CAD Studio notices the copilot is gone.
  for (const pid of extraPids.splice(0)) killProcessTree(pid);
  await expect(studio.page.getByTestId('status-copilot')).toHaveAttribute('data-connected', 'false', { timeout: 15_000 });
  expect(childAppProcesses(studio.pid, 'copilot')).toEqual([]);
});

test('h. CAD Studio dies in the middle of a tool call: the copilot reports it and stays usable', async () => {
  presetSettings({ autoConnect: true, autoDock: false });
  const studio = await startStudio();
  const { page } = await startCopilot([
    {
      text: 'Drawing the table now.',
      toolCalls: [{ name: 'create_entities', input: { unit: 'ft', entities: [{ type: 'circle', center: { x: 0, y: 0 }, radius: 2 }] } }],
    },
    { text: 'CAD Studio stopped responding, so the table may not have been drawn.' },
    { text: 'I am still here.' },
  ]);
  await expectConnectedTo(page, studio);

  // Freeze CAD Studio's drawing view so the tool call stays in flight.
  void studio.page
    .evaluate(() => {
      const end = Date.now() + 120_000;
      while (Date.now() < end) {
        // Busy: the renderer cannot answer the tool call.
      }
    })
    .catch(() => undefined);
  await send(page, 'Draw a round table');
  const row = page.getByTestId('activity-row').last();
  await expect(row).toHaveAttribute('data-status', 'running');
  await shot(page, 'h1-call-in-flight-copilot');

  await killStudio(studio);
  studios.splice(studios.indexOf(studio), 1);

  await expect(row).toHaveAttribute('data-status', 'failed', { timeout: 20_000 });
  await expect(page.getByTestId('message-assistant').last()).toContainText('stopped responding', { timeout: 20_000 });
  await waitIdle(page);
  await expect(page.getByTestId('connection-chip')).toHaveAttribute('data-status', 'disconnected');
  await shot(page, 'h2-cad-died-copilot');

  await send(page, 'Are you still there?');
  await expect(page.getByTestId('message-assistant').last()).toContainText('I am still here.');
  await waitIdle(page);
});
