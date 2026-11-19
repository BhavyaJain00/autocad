import { beforeEach, describe, expect, it } from 'vitest';
import type { WindowBounds } from '@cadstudio/cad-protocol';
import { PANEL_WIDTH_MAX, PANEL_WIDTH_MIN } from '../shared/bridge';
import {
  CAD_HARD_MIN_WIDTH,
  CAD_PREFERRED_MIN_WIDTH,
  computeDockLayout,
  DISPLAY_TOO_NARROW_MESSAGE,
  dockedPanelWidth,
  DockingController,
  NO_CAD_WINDOW_MESSAGE,
  NO_COPILOT_WINDOW_MESSAGE,
  NOT_CONNECTED_MESSAGE,
  outerBoundsForVisible,
  WINDOWS_INVISIBLE_FRAME,
  type DockConnection,
  type DockScreen,
  type DockWindow,
  type Rect,
} from './docking';

const FULL_HD: Rect = { x: 0, y: 0, width: 1920, height: 1040 };

describe('dockedPanelWidth', () => {
  it('keeps a requested width inside the limits', () => {
    expect(dockedPanelWidth(460, 1920)).toBe(460);
    expect(dockedPanelWidth(100, 1920)).toBe(PANEL_WIDTH_MIN);
    expect(dockedPanelWidth(5000, 1920)).toBe(PANEL_WIDTH_MAX);
    expect(dockedPanelWidth(500.6, 1920)).toBe(501);
  });

  it('never takes more than 55% of the work area', () => {
    expect(dockedPanelWidth(760, 1000)).toBe(550);
    expect(dockedPanelWidth(760, 1280)).toBe(704);
    expect(dockedPanelWidth(460, 1000)).toBe(460);
  });

  it('never goes below the minimum, even on a very narrow display', () => {
    expect(dockedPanelWidth(460, 600)).toBe(PANEL_WIDTH_MIN);
    expect(dockedPanelWidth(Number.NaN, 1920)).toBe(PANEL_WIDTH_MIN);
  });
});

describe('computeDockLayout', () => {
  it('puts the panel in a full-height column on the right and the CAD on the left', () => {
    expect(computeDockLayout(FULL_HD, 460)).toEqual({
      panelWidth: 460,
      cad: { x: 0, y: 0, width: 1460, height: 1040 },
      panel: { x: 1460, y: 0, width: 460, height: 1040 },
    });
  });

  it('respects the origin of a secondary display and a taskbar offset', () => {
    const layout = computeDockLayout({ x: -1280, y: 40, width: 1280, height: 984 }, 500);
    expect(layout).toEqual({
      panelWidth: 500,
      cad: { x: -1280, y: 40, width: 780, height: 984 },
      panel: { x: -500, y: 40, width: 500, height: 984 },
    });
  });

  it('the two rectangles tile the work area exactly', () => {
    for (const width of [1024, 1280, 1366, 1536, 1920, 2560, 3440]) {
      for (const requested of [0, 380, 460, 600, 760, 2000]) {
        const area = { x: 13, y: 7, width, height: 800 };
        const layout = computeDockLayout(area, requested);
        expect(layout, `${width}/${requested}`).not.toBeNull();
        if (layout === null) continue;
        expect(layout.cad.x).toBe(area.x);
        expect(layout.cad.x + layout.cad.width).toBe(layout.panel.x);
        expect(layout.panel.x + layout.panel.width).toBe(area.x + area.width);
        expect(layout.cad.y).toBe(area.y);
        expect(layout.panel.y).toBe(area.y);
        expect(layout.cad.height).toBe(area.height);
        expect(layout.panel.height).toBe(area.height);
        expect(layout.panel.width).toBe(layout.panelWidth);
        expect(layout.panelWidth).toBeGreaterThanOrEqual(PANEL_WIDTH_MIN);
        expect(layout.panelWidth).toBeLessThanOrEqual(Math.min(PANEL_WIDTH_MAX, Math.floor(width * 0.55)));
        expect(layout.cad.width).toBeGreaterThanOrEqual(CAD_HARD_MIN_WIDTH);
      }
    }
  });

  it('clamps the panel to its maximum and to 55% of the work area', () => {
    expect(computeDockLayout(FULL_HD, 9999)?.panelWidth).toBe(PANEL_WIDTH_MAX);
    expect(computeDockLayout({ x: 0, y: 0, width: 1200, height: 800 }, 760)?.panelWidth).toBe(660);
  });

  it('narrows the panel to leave the CAD its preferred minimum width', () => {
    // 900 wide: 55% allows 495, but that would leave the CAD only 405.
    const layout = computeDockLayout({ x: 0, y: 0, width: 900, height: 700 }, 495);
    expect(layout?.panelWidth).toBe(900 - CAD_PREFERRED_MIN_WIDTH);
    expect(layout?.cad.width).toBe(CAD_PREFERRED_MIN_WIDTH);
  });

  it('never narrows the panel below its minimum', () => {
    const layout = computeDockLayout({ x: 0, y: 0, width: 800, height: 600 }, 460);
    expect(layout?.panelWidth).toBe(PANEL_WIDTH_MIN);
    expect(layout?.cad.width).toBe(420);
  });

  it('refuses a display that leaves the CAD less than its hard minimum', () => {
    expect(computeDockLayout({ x: 0, y: 0, width: PANEL_WIDTH_MIN + CAD_HARD_MIN_WIDTH, height: 600 }, 460)).not.toBeNull();
    expect(computeDockLayout({ x: 0, y: 0, width: PANEL_WIDTH_MIN + CAD_HARD_MIN_WIDTH - 1, height: 600 }, 460)).toBeNull();
    expect(computeDockLayout({ x: 0, y: 0, width: 640, height: 480 }, 460)).toBeNull();
  });

  it('refuses unusable work areas', () => {
    expect(computeDockLayout({ x: 0, y: 0, width: 0, height: 0 }, 460)).toBeNull();
    expect(computeDockLayout({ x: 0, y: 0, width: 1920, height: -5 }, 460)).toBeNull();
    expect(computeDockLayout({ x: Number.NaN, y: 0, width: 1920, height: 1040 }, 460)).toBeNull();
  });

  it('rounds fractional work areas to whole DIP', () => {
    const layout = computeDockLayout({ x: 0.4, y: 0.6, width: 1919.7, height: 1039.5 }, 460.2);
    expect(layout).toEqual({
      panelWidth: 460,
      cad: { x: 0, y: 1, width: 1460, height: 1040 },
      panel: { x: 1460, y: 1, width: 460, height: 1040 },
    });
  });
});

// ---------------------------------------------------------------------------------------------

class FakeWindow implements DockWindow {
  destroyed = false;
  minimized = false;
  maximized = false;
  fullScreen = false;
  bounds: Rect = { x: 1300, y: 100, width: 460, height: 820 };
  readonly log: string[] = [];

  isDestroyed(): boolean {
    return this.destroyed;
  }
  isMinimized(): boolean {
    return this.minimized;
  }
  restore(): void {
    this.log.push('restore');
    this.minimized = false;
  }
  isMaximized(): boolean {
    return this.maximized;
  }
  unmaximize(): void {
    this.log.push('unmaximize');
    this.maximized = false;
  }
  isFullScreen(): boolean {
    return this.fullScreen;
  }
  setFullScreen(flag: boolean): void {
    this.log.push(`setFullScreen(${flag})`);
    this.fullScreen = flag;
  }
  getBounds(): Rect {
    return { ...this.bounds };
  }
  setBounds(bounds: Rect): void {
    this.log.push(`setBounds(${bounds.x},${bounds.y},${bounds.width},${bounds.height})`);
    this.bounds = { ...bounds };
  }
}

class FakeConnection implements DockConnection {
  bounds: WindowBounds | null = { x: 150, y: 75, width: 1500, height: 900 };
  readonly setCalls: WindowBounds[] = [];
  failSet: Error | null = null;
  failGet: Error | null = null;
  /** When set, setWindowBounds waits for this promise first. */
  gate: Promise<void> | null = null;
  hang = false;

  async getWindowBounds(): Promise<WindowBounds | null> {
    if (this.failGet !== null) throw this.failGet;
    return this.bounds === null ? null : { ...this.bounds };
  }
  async setWindowBounds(bounds: WindowBounds): Promise<void> {
    this.setCalls.push({ ...bounds });
    if (this.hang) await new Promise(() => undefined);
    if (this.gate !== null) await this.gate;
    if (this.failSet !== null) throw this.failSet;
    this.bounds = { ...bounds };
  }
}

/** A display at `scale`: physical pixels = DIP * scale. */
function createScreen(workArea: Rect, scale = 1): DockScreen & { matched: Rect[] } {
  const matched: Rect[] = [];
  const multiply = (rect: Rect, factor: number): Rect => ({
    x: rect.x * factor,
    y: rect.y * factor,
    width: rect.width * factor,
    height: rect.height * factor,
  });
  return {
    matched,
    screenToDipRect: (_window, rect) => multiply(rect, 1 / scale),
    dipToScreenRect: (_window, rect) => multiply(rect, scale),
    getDisplayMatching: (rect) => {
      matched.push({ ...rect });
      return { workArea: { ...workArea } };
    },
  };
}

interface Harness {
  controller: DockingController;
  win: FakeWindow;
  connection: FakeConnection;
  screen: ReturnType<typeof createScreen>;
  dockedChanges: boolean[];
  persisted: number[];
  logs: string[];
  state: { window: FakeWindow | null; connection: FakeConnection | null; panelWidth: number };
}

function createHarness(options: { workArea?: Rect; scale?: number; panelWidth?: number; cadFrameInset?: number } = {}): Harness {
  const win = new FakeWindow();
  const connection = new FakeConnection();
  const screen = createScreen(options.workArea ?? FULL_HD, options.scale ?? 1);
  const state: Harness['state'] = { window: win, connection, panelWidth: options.panelWidth ?? 460 };
  const dockedChanges: boolean[] = [];
  const persisted: number[] = [];
  const logs: string[] = [];
  const controller = new DockingController({
    screen,
    getWindow: () => state.window,
    getConnection: () => state.connection,
    getPanelWidth: () => state.panelWidth,
    setPanelWidth: (width) => {
      persisted.push(width);
      state.panelWidth = width;
    },
    onDockedChanged: (docked) => dockedChanges.push(docked),
    log: (message) => logs.push(message),
    requestTimeoutMs: 40,
    restoreTimeoutMs: 40,
    ...(options.cadFrameInset !== undefined ? { cadFrameInset: options.cadFrameInset } : {}),
  });
  return { controller, win, connection, screen, dockedChanges, persisted, logs, state };
}

describe('outerBoundsForVisible', () => {
  it('grows the rectangle by the invisible border on the left, right and bottom only', () => {
    expect(outerBoundsForVisible({ x: 0, y: 0, width: 906, height: 728 }, WINDOWS_INVISIBLE_FRAME)).toEqual({
      x: -7,
      y: 0,
      width: 920,
      height: 735,
    });
  });

  it('leaves the rectangle unchanged for no, negative or invalid insets', () => {
    const visible = { x: 10, y: 20, width: 300, height: 200 };
    for (const inset of [0, -3, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(outerBoundsForVisible(visible, inset)).toEqual(visible);
    }
  });
});

describe('DockingController with an invisible CAD frame (Windows)', () => {
  it('places the CAD window so its visible frame fills the left part and meets the copilot', async () => {
    const h = createHarness({ cadFrameInset: WINDOWS_INVISIBLE_FRAME });
    expect(await h.controller.dock()).toEqual({ ok: true });
    expect(h.connection.setCalls).toEqual([{ x: -7, y: 0, width: 1474, height: 1047 }]);
    expect(h.win.bounds).toEqual({ x: 1460, y: 0, width: 460, height: 1040 });
    // The layout itself stays the visible split of the work area.
    expect(h.controller.layout?.cad).toEqual({ x: 0, y: 0, width: 1460, height: 1040 });
  });

  it('scales the border to physical pixels and restores the exact previous bounds on undock', async () => {
    const h = createHarness({ cadFrameInset: WINDOWS_INVISIBLE_FRAME, scale: 2, workArea: { x: 0, y: 0, width: 1280, height: 680 } });
    h.connection.bounds = { x: 300, y: 150, width: 1200, height: 600 };
    expect(await h.controller.dock()).toEqual({ ok: true });
    expect(h.connection.setCalls[0]).toEqual({ x: -14, y: 0, width: 1668, height: 1374 });
    expect(await h.controller.undock()).toEqual({ ok: true });
    expect(h.connection.setCalls[1]).toEqual({ x: 300, y: 150, width: 1200, height: 600 });
  });

  it('keeps the compensation when the user resizes the docked panel', async () => {
    const h = createHarness({ cadFrameInset: WINDOWS_INVISIBLE_FRAME });
    await h.controller.dock();
    h.win.bounds = { x: 1400, y: 0, width: 520, height: 1040 };
    await h.controller.windowResized();
    expect(h.connection.setCalls.at(-1)).toEqual({ x: -7, y: 0, width: 1414, height: 1047 });
  });
});

describe('DockingController', () => {
  let h: Harness;

  beforeEach(() => {
    h = createHarness();
  });

  describe('dock', () => {
    it('moves the CAD to the left part and the copilot to the right column', async () => {
      expect(await h.controller.dock()).toEqual({ ok: true });
      expect(h.connection.setCalls).toEqual([{ x: 0, y: 0, width: 1460, height: 1040 }]);
      expect(h.win.bounds).toEqual({ x: 1460, y: 0, width: 460, height: 1040 });
      expect(h.controller.docked).toBe(true);
      expect(h.dockedChanges).toEqual([true]);
      // The display is the one the CAD window is on.
      expect(h.screen.matched).toEqual([{ x: 150, y: 75, width: 1500, height: 900 }]);
    });

    it('converts between physical pixels and DIP on a scaled display', async () => {
      h = createHarness({ scale: 1.5, workArea: { x: 0, y: 0, width: 1280, height: 680 } });
      h.connection.bounds = { x: 300, y: 150, width: 1200, height: 600 };
      expect(await h.controller.dock()).toEqual({ ok: true });
      // The display lookup happens in DIP...
      expect(h.screen.matched[0]).toEqual({ x: 200, y: 100, width: 800, height: 400 });
      // ...the CAD receives physical pixels...
      expect(h.connection.setCalls).toEqual([{ x: 0, y: 0, width: 1230, height: 1020 }]);
      // ...and the copilot window is placed in DIP.
      expect(h.win.bounds).toEqual({ x: 820, y: 0, width: 460, height: 680 });
    });

    it('works without the conversion functions (DIP equals pixels)', async () => {
      const screen: DockScreen = { getDisplayMatching: () => ({ workArea: { ...FULL_HD } }) };
      const win = new FakeWindow();
      const connection = new FakeConnection();
      const controller = new DockingController({
        screen,
        getWindow: () => win,
        getConnection: () => connection,
        getPanelWidth: () => 500,
        setPanelWidth: () => undefined,
        onDockedChanged: () => undefined,
      });
      expect(await controller.dock()).toEqual({ ok: true });
      expect(connection.setCalls).toEqual([{ x: 0, y: 0, width: 1420, height: 1040 }]);
      expect(win.bounds).toEqual({ x: 1420, y: 0, width: 500, height: 1040 });
    });

    it('leaves the maximised, minimised or full-screen state before placing the window', async () => {
      h.win.minimized = true;
      h.win.maximized = true;
      h.win.fullScreen = true;
      await h.controller.dock();
      expect(h.win.log).toEqual(['restore', 'setFullScreen(false)', 'unmaximize', 'setBounds(1460,0,460,1040)']);
    });

    it('fails without a connection', async () => {
      h.state.connection = null;
      expect(await h.controller.dock()).toEqual({ ok: false, message: NOT_CONNECTED_MESSAGE });
      expect(h.controller.docked).toBe(false);
      expect(h.dockedChanges).toEqual([]);
    });

    it('fails without a copilot window', async () => {
      h.state.window = null;
      expect(await h.controller.dock()).toEqual({ ok: false, message: NO_COPILOT_WINDOW_MESSAGE });
      h.state.window = h.win;
      h.win.destroyed = true;
      expect(await h.controller.dock()).toEqual({ ok: false, message: NO_COPILOT_WINDOW_MESSAGE });
      expect(h.connection.setCalls).toEqual([]);
    });

    it('fails when the CAD has no window', async () => {
      h.connection.bounds = null;
      expect(await h.controller.dock()).toEqual({ ok: false, message: NO_CAD_WINDOW_MESSAGE });
      expect(h.win.log).toEqual([]);
    });

    it('fails on a display that is too narrow, without moving anything', async () => {
      h = createHarness({ workArea: { x: 0, y: 0, width: 700, height: 500 } });
      expect(await h.controller.dock()).toEqual({ ok: false, message: DISPLAY_TOO_NARROW_MESSAGE });
      expect(h.connection.setCalls).toEqual([]);
      expect(h.win.log).toEqual([]);
      expect(h.controller.docked).toBe(false);
    });

    it('returns the failure when the CAD rejects the new bounds and leaves the copilot window alone', async () => {
      h.connection.failSet = new Error('CAD Studio has no window open.');
      const result = await h.controller.dock();
      expect(result).toEqual({ ok: false, message: 'Docking failed: CAD Studio has no window open.' });
      expect(h.win.log).toEqual([]);
      expect(h.controller.docked).toBe(false);
    });

    it('returns a failure when reading the CAD bounds fails', async () => {
      h.connection.failGet = new Error('connection lost');
      expect(await h.controller.dock()).toEqual({ ok: false, message: 'Docking failed: connection lost' });
    });

    it('times out when the CAD never answers', async () => {
      h.connection.hang = true;
      const result = await h.controller.dock();
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.message).toContain('did not answer in time');
      expect(h.controller.docked).toBe(false);
    });

    it('does not dock when the connection goes away while the CAD is answering', async () => {
      let release: () => void = () => undefined;
      h.connection.gate = new Promise((resolve) => {
        release = resolve;
      });
      const pending = h.controller.dock();
      await new Promise((resolve) => setTimeout(resolve, 5));
      h.state.connection = null;
      release();
      const result = await pending;
      expect(result.ok).toBe(false);
      expect(h.controller.docked).toBe(false);
      expect(h.win.log).toEqual([]);
    });

    it('re-fits to a new panel width while docked, keeping the bounds remembered for undocking', async () => {
      await h.controller.dock();
      h.state.panelWidth = 600;
      expect(await h.controller.dock()).toEqual({ ok: true });
      expect(h.connection.setCalls.at(-1)).toEqual({ x: 0, y: 0, width: 1320, height: 1040 });
      expect(h.win.bounds).toEqual({ x: 1320, y: 0, width: 600, height: 1040 });
      // Still one dock: no second notification, and the CAD bounds are not read again.
      expect(h.dockedChanges).toEqual([true]);

      await h.controller.undock();
      expect(h.connection.setCalls.at(-1)).toEqual({ x: 150, y: 75, width: 1500, height: 900 });
    });

    it('runs operations one at a time', async () => {
      const results = await Promise.all([h.controller.dock(), h.controller.undock(), h.controller.dock()]);
      expect(results).toEqual([{ ok: true }, { ok: true }, { ok: true }]);
      expect(h.dockedChanges).toEqual([true, false, true]);
      expect(h.controller.docked).toBe(true);
    });
  });

  describe('undock', () => {
    it('restores the CAD window and the copilot window', async () => {
      await h.controller.dock();
      expect(await h.controller.undock()).toEqual({ ok: true });
      expect(h.connection.setCalls.at(-1)).toEqual({ x: 150, y: 75, width: 1500, height: 900 });
      expect(h.win.bounds).toEqual({ x: 1300, y: 100, width: 460, height: 820 });
      expect(h.controller.docked).toBe(false);
      expect(h.dockedChanges).toEqual([true, false]);
    });

    it('restores the copilot window at the panel width chosen while docked, keeping its right edge', async () => {
      await h.controller.dock();
      h.state.panelWidth = 600;
      await h.controller.dock();
      await h.controller.undock();
      expect(h.win.bounds).toEqual({ x: 1160, y: 100, width: 600, height: 820 });
    });

    it('leaves a copilot window the user moved where it is', async () => {
      await h.controller.dock();
      h.win.bounds = { x: 200, y: 200, width: 460, height: 700 };
      await h.controller.undock();
      expect(h.win.bounds).toEqual({ x: 200, y: 200, width: 460, height: 700 });
    });

    it('is a no-op when not docked', async () => {
      expect(await h.controller.undock()).toEqual({ ok: true });
      expect(h.connection.setCalls).toEqual([]);
      expect(h.dockedChanges).toEqual([]);
    });

    it('releases the dock even when the CAD cannot be restored', async () => {
      await h.controller.dock();
      h.connection.failSet = new Error('pipe closed');
      const result = await h.controller.undock();
      expect(result).toEqual({ ok: false, message: 'The CAD window could not be restored: pipe closed' });
      expect(h.controller.docked).toBe(false);
      expect(h.dockedChanges).toEqual([true, false]);
      expect(h.logs).toHaveLength(1);
    });

    it('gives up restoring after a short timeout', async () => {
      await h.controller.dock();
      h.connection.hang = true;
      const started = Date.now();
      const result = await h.controller.undock();
      expect(result.ok).toBe(false);
      expect(Date.now() - started).toBeLessThan(1000);
      expect(h.controller.docked).toBe(false);
    });

    it('restores through the connection that was docked, even after it was replaced', async () => {
      await h.controller.dock();
      const replacement = new FakeConnection();
      h.state.connection = replacement;
      await h.controller.undock();
      expect(h.connection.setCalls.at(-1)).toEqual({ x: 150, y: 75, width: 1500, height: 900 });
      expect(replacement.setCalls).toEqual([]);
    });

    it('survives a copilot window that is gone', async () => {
      await h.controller.dock();
      h.win.destroyed = true;
      expect(await h.controller.undock()).toEqual({ ok: true });
      expect(h.connection.bounds).toEqual({ x: 150, y: 75, width: 1500, height: 900 });
    });
  });

  describe('forget', () => {
    it('drops the dock without touching either window', async () => {
      await h.controller.dock();
      const calls = h.connection.setCalls.length;
      const windowLog = h.win.log.length;
      h.controller.forget();
      expect(h.controller.docked).toBe(false);
      expect(h.dockedChanges).toEqual([true, false]);
      expect(h.connection.setCalls).toHaveLength(calls);
      expect(h.win.log).toHaveLength(windowLog);
      // Nothing left to restore.
      await h.controller.undock();
      expect(h.connection.setCalls).toHaveLength(calls);
    });

    it('does nothing when not docked', () => {
      h.controller.forget();
      expect(h.dockedChanges).toEqual([]);
    });

    it('a dock to a replaced connection starts from scratch', async () => {
      await h.controller.dock();
      const replacement = new FakeConnection();
      replacement.bounds = { x: 10, y: 10, width: 800, height: 600 };
      h.state.connection = replacement;
      expect(await h.controller.dock()).toEqual({ ok: true });
      expect(replacement.setCalls).toEqual([{ x: 0, y: 0, width: 1460, height: 1040 }]);
      await h.controller.undock();
      expect(replacement.setCalls.at(-1)).toEqual({ x: 10, y: 10, width: 800, height: 600 });
    });
  });

  describe('windowResized', () => {
    it('makes a new width the panel width: persists it and re-fits both windows', async () => {
      await h.controller.dock();
      // The user drags the left edge 100 px to the left.
      h.win.bounds = { x: 1360, y: 0, width: 560, height: 1040 };
      await h.controller.windowResized();
      expect(h.persisted).toEqual([560]);
      expect(h.connection.setCalls.at(-1)).toEqual({ x: 0, y: 0, width: 1360, height: 1040 });
      expect(h.win.bounds).toEqual({ x: 1360, y: 0, width: 560, height: 1040 });
      expect(h.controller.layout?.panelWidth).toBe(560);
    });

    it('snaps the window back into the right column when it was resized from the right', async () => {
      await h.controller.dock();
      h.win.bounds = { x: 1460, y: 0, width: 400, height: 1040 };
      await h.controller.windowResized();
      expect(h.win.bounds).toEqual({ x: 1520, y: 0, width: 400, height: 1040 });
      expect(h.connection.setCalls.at(-1)).toEqual({ x: 0, y: 0, width: 1520, height: 1040 });
    });

    it('clamps an oversized width and persists the clamped value', async () => {
      await h.controller.dock();
      h.win.bounds = { x: 400, y: 0, width: 1520, height: 1040 };
      await h.controller.windowResized();
      expect(h.persisted).toEqual([PANEL_WIDTH_MAX]);
      expect(h.win.bounds.width).toBe(PANEL_WIDTH_MAX);
    });

    it('ignores moves, height changes and one-pixel rounding differences', async () => {
      await h.controller.dock();
      const calls = h.connection.setCalls.length;
      h.win.bounds = { x: 900, y: 50, width: 460, height: 700 };
      await h.controller.windowResized();
      h.win.bounds = { x: 1459, y: 0, width: 461, height: 1040 };
      await h.controller.windowResized();
      expect(h.persisted).toEqual([]);
      expect(h.connection.setCalls).toHaveLength(calls);
    });

    it('ignores a minimised, maximised or full-screen window', async () => {
      await h.controller.dock();
      const calls = h.connection.setCalls.length;
      h.win.bounds = { x: 0, y: 0, width: 1920, height: 1040 };
      h.win.maximized = true;
      await h.controller.windowResized();
      h.win.maximized = false;
      h.win.minimized = true;
      await h.controller.windowResized();
      h.win.minimized = false;
      h.win.fullScreen = true;
      await h.controller.windowResized();
      expect(h.persisted).toEqual([]);
      expect(h.connection.setCalls).toHaveLength(calls);
    });

    it('does nothing when not docked', async () => {
      h.win.bounds = { x: 0, y: 0, width: 700, height: 800 };
      await h.controller.windowResized();
      expect(h.persisted).toEqual([]);
      expect(h.connection.setCalls).toEqual([]);
    });

    it('does not persist a width the display forced to be narrower than chosen', async () => {
      h = createHarness({ workArea: { x: 0, y: 0, width: 900, height: 700 }, panelWidth: 400 });
      await h.controller.dock();
      expect(h.controller.layout?.panelWidth).toBe(400);
      // Dragged to 490: allowed by the 55% rule (495) but the CAD must keep 480.
      h.win.bounds = { x: 410, y: 0, width: 490, height: 700 };
      await h.controller.windowResized();
      expect(h.persisted).toEqual([]);
      expect(h.controller.layout?.panelWidth).toBe(420);
      expect(h.win.bounds).toEqual({ x: 480, y: 0, width: 420, height: 700 });
    });

    it('stays docked and logs when the CAD cannot be re-fitted', async () => {
      await h.controller.dock();
      h.connection.failSet = new Error('busy');
      h.win.bounds = { x: 1400, y: 0, width: 520, height: 1040 };
      await h.controller.windowResized();
      expect(h.controller.docked).toBe(true);
      expect(h.logs.some((line) => line.includes('busy'))).toBe(true);
    });
  });

  it('a failing dock-state listener does not break docking', async () => {
    const win = new FakeWindow();
    const connection = new FakeConnection();
    const logs: string[] = [];
    const controller = new DockingController({
      screen: createScreen(FULL_HD),
      getWindow: () => win,
      getConnection: () => connection,
      getPanelWidth: () => 460,
      setPanelWidth: () => undefined,
      onDockedChanged: () => {
        throw new Error('listener broke');
      },
      log: (message) => logs.push(message),
    });
    expect(await controller.dock()).toEqual({ ok: true });
    expect(controller.docked).toBe(true);
    expect(logs).toHaveLength(1);
  });
});
