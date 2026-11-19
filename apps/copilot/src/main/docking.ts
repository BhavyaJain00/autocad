/**
 * Docking: the CAD window takes the left part of its display's work area and the copilot window
 * a full-height column on the right. The geometry is a pure function; the controller talks to
 * the CAD connector (physical pixels) and to the copilot window (DIP) through small interfaces,
 * so everything is unit-testable without Electron.
 */
import type { WindowBounds } from '@cadstudio/cad-protocol';
import { PANEL_WIDTH_MAX, PANEL_WIDTH_MIN, type ActionResult } from '../shared/bridge';
import { errorMessage } from './json-file';

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Largest share of the work area's width the panel may take. */
export const PANEL_MAX_WORK_AREA_SHARE = 0.55;
/** Width (DIP) the CAD window should keep; a wider panel is narrowed to leave this much. */
export const CAD_PREFERRED_MIN_WIDTH = 480;
/** Width (DIP) below which the CAD window is not usable: docking is refused instead. */
export const CAD_HARD_MIN_WIDTH = 400;
/** Width difference (DIP) below which the docked window does not count as resized by the user. */
export const RESIZE_TOLERANCE = 1;
/** Time allowed for the connector to answer while docking. */
export const DOCK_REQUEST_TIMEOUT_MS = 5_000;
/** Time allowed for restoring the CAD window (undock, disconnect, quit): best effort. */
export const RESTORE_TIMEOUT_MS = 1_500;
/**
 * Width (DIP) of the invisible resize border Windows 10 and 11 put around a standard framed
 * window on its left, right and bottom. The protocol's WindowBounds are the outer bounds
 * (GetWindowRect), which include that border, so a CAD window placed exactly in the work area
 * would show a 7 px strip of desktop on its left, below it and between it and the copilot.
 */
export const WINDOWS_INVISIBLE_FRAME = 7;

export const DISPLAY_TOO_NARROW_MESSAGE =
  'This display is too narrow to dock CAD Copilot beside the CAD window.';
export const NOT_CONNECTED_MESSAGE = 'Connect to a CAD application first.';
export const NO_CAD_WINDOW_MESSAGE = 'The CAD application has no window to dock beside.';
export const NO_COPILOT_WINDOW_MESSAGE = 'The CAD Copilot window is not available.';

export interface DockLayout {
  /** Width of the copilot column (DIP). */
  readonly panelWidth: number;
  /** Bounds of the CAD window (DIP): the work area minus the panel. */
  readonly cad: Rect;
  /** Bounds of the copilot window (DIP): the right column at full work-area height. */
  readonly panel: Rect;
}

/**
 * The panel width used on a display: the requested width inside
 * [PANEL_WIDTH_MIN, min(PANEL_WIDTH_MAX, 55% of the work area)], whole DIP. On a display so
 * narrow that 55% is less than the minimum, the minimum wins (the window cannot be narrower).
 */
export function dockedPanelWidth(requestedWidth: number, workAreaWidth: number): number {
  const upper = Math.max(PANEL_WIDTH_MIN, Math.min(PANEL_WIDTH_MAX, Math.floor(workAreaWidth * PANEL_MAX_WORK_AREA_SHARE)));
  const requested = Number.isFinite(requestedWidth) ? Math.round(requestedWidth) : PANEL_WIDTH_MIN;
  return Math.min(upper, Math.max(PANEL_WIDTH_MIN, requested));
}

/**
 * Splits a display's work area (DIP) between the CAD window (left) and the copilot panel (right).
 * The panel is narrowed (never below its minimum) when the CAD would get less than
 * CAD_PREFERRED_MIN_WIDTH. Returns null when the CAD would still get less than
 * CAD_HARD_MIN_WIDTH or the work area is not a usable rectangle.
 */
export function computeDockLayout(workArea: Rect, requestedPanelWidth: number): DockLayout | null {
  const area = {
    x: Math.round(workArea.x),
    y: Math.round(workArea.y),
    width: Math.round(workArea.width),
    height: Math.round(workArea.height),
  };
  if (![area.x, area.y, area.width, area.height].every(Number.isFinite) || area.width <= 0 || area.height <= 0) {
    return null;
  }
  let panelWidth = dockedPanelWidth(requestedPanelWidth, area.width);
  if (area.width - panelWidth < CAD_PREFERRED_MIN_WIDTH) {
    panelWidth = Math.max(PANEL_WIDTH_MIN, area.width - CAD_PREFERRED_MIN_WIDTH);
  }
  const cadWidth = area.width - panelWidth;
  if (cadWidth < CAD_HARD_MIN_WIDTH) return null;
  return {
    panelWidth,
    cad: { x: area.x, y: area.y, width: cadWidth, height: area.height },
    panel: { x: area.x + cadWidth, y: area.y, width: panelWidth, height: area.height },
  };
}

/**
 * The outer bounds (DIP) to give a CAD window whose VISIBLE frame should cover `visible`, when the
 * window has an invisible border of `inset` DIP on its left, right and bottom (none at the top).
 */
export function outerBoundsForVisible(visible: Rect, inset: number): Rect {
  const border = Number.isFinite(inset) && inset > 0 ? inset : 0;
  return {
    x: visible.x - border,
    y: visible.y,
    width: visible.width + 2 * border,
    height: visible.height + border,
  };
}

function roundRect(rect: Rect): Rect {
  return {
    x: Math.round(rect.x),
    y: Math.round(rect.y),
    width: Math.round(rect.width),
    height: Math.round(rect.height),
  };
}

function sameRect(a: Rect, b: Rect): boolean {
  return a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;
}

/**
 * The subset of Electron's `screen` module used for docking. The two conversions exist on
 * Windows and Linux only; where they are missing, DIP and screen pixels are the same thing.
 */
export interface DockScreen {
  screenToDipRect?(window: null, rect: Rect): Rect;
  dipToScreenRect?(window: null, rect: Rect): Rect;
  getDisplayMatching(rect: Rect): { workArea: Rect };
}

/** The subset of BrowserWindow used for docking (the copilot's own window). */
export interface DockWindow {
  isDestroyed(): boolean;
  isMinimized(): boolean;
  restore(): void;
  isMaximized(): boolean;
  unmaximize(): void;
  isFullScreen(): boolean;
  setFullScreen(flag: boolean): void;
  /** Outer bounds in DIP. */
  getBounds(): Rect;
  setBounds(bounds: Rect): void;
}

/** The window methods of a CadConnection. */
export interface DockConnection {
  getWindowBounds(): Promise<WindowBounds | null>;
  setWindowBounds(bounds: WindowBounds): Promise<void>;
}

export interface DockingOptions {
  readonly screen: DockScreen;
  /** The copilot window, or null when there is none. */
  getWindow(): DockWindow | null;
  /** The connected CAD, or null. */
  getConnection(): DockConnection | null;
  /** The panel width from the settings (DIP). */
  getPanelWidth(): number;
  /** Persists a panel width the user chose by resizing the docked window. */
  setPanelWidth(width: number): void;
  /** Called whenever the docked state changes. */
  onDockedChanged(docked: boolean): void;
  /**
   * Invisible border (DIP) of the CAD window on its left, right and bottom, which docking places
   * outside the work area / under the copilot so the visible edges meet. Default 0; on Windows
   * pass {@link WINDOWS_INVISIBLE_FRAME}.
   */
  readonly cadFrameInset?: number;
  readonly log?: (message: string) => void;
  /** Overrides DOCK_REQUEST_TIMEOUT_MS (tests). */
  readonly requestTimeoutMs?: number;
  /** Overrides RESTORE_TIMEOUT_MS (tests). */
  readonly restoreTimeoutMs?: number;
}

interface DockState {
  /** The connection that was docked (restoring goes to it, even while it is being replaced). */
  readonly connection: DockConnection;
  /** The CAD window's bounds before docking (physical pixels). */
  readonly cadPreviousBounds: WindowBounds;
  /** The copilot window's bounds before docking (DIP). */
  readonly panelPreviousBounds: Rect;
  /** The layout currently applied. */
  layout: DockLayout;
}

/** Rejects with a readable error when `promise` takes longer than `timeoutMs`. */
function withTimeout<T>(promise: Promise<T>, timeoutMs: number, what: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      reject(new Error(`The CAD application did not answer in time (${what}).`));
    }, timeoutMs);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timer);
        reject(error instanceof Error ? error : new Error(String(error)));
      },
    );
  });
}

/**
 * Docks and undocks the copilot beside the connected CAD window. Operations run one at a time;
 * failures are returned as `{ ok: false, message }` and never thrown.
 */
export class DockingController {
  readonly #options: DockingOptions;
  readonly #log: (message: string) => void;
  readonly #requestTimeoutMs: number;
  readonly #restoreTimeoutMs: number;
  #state: DockState | null = null;
  #queue: Promise<unknown> = Promise.resolve();

  constructor(options: DockingOptions) {
    this.#options = options;
    this.#log = options.log ?? (() => undefined);
    this.#requestTimeoutMs = options.requestTimeoutMs ?? DOCK_REQUEST_TIMEOUT_MS;
    this.#restoreTimeoutMs = options.restoreTimeoutMs ?? RESTORE_TIMEOUT_MS;
  }

  /** True while the copilot is docked beside the CAD window. */
  get docked(): boolean {
    return this.#state !== null;
  }

  /** The layout applied while docked, otherwise null. */
  get layout(): DockLayout | null {
    return this.#state?.layout ?? null;
  }

  /**
   * Docks beside the CAD window. While already docked, re-fits both windows to the panel width
   * from the settings (the bounds remembered for undocking are kept).
   */
  dock(): Promise<ActionResult> {
    return this.#enqueue(() => this.#dock());
  }

  /** Restores the CAD window (and the copilot window) to their bounds before docking. */
  undock(): Promise<ActionResult> {
    return this.#enqueue(() => this.#undock());
  }

  /**
   * The user resized the copilot window (call this debounced). While docked, a new width becomes
   * the panel width: it is persisted and both windows are re-fitted.
   */
  windowResized(): Promise<void> {
    return this.#enqueue(() => this.#windowResized());
  }

  /** The connection is gone: the CAD window can no longer be restored, so forget the dock. */
  forget(): void {
    if (this.#state === null) return;
    this.#state = null;
    this.#notify(false);
  }

  #enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const run = this.#queue.then(operation, operation);
    this.#queue = run.catch(() => undefined);
    return run;
  }

  #notify(docked: boolean): void {
    try {
      this.#options.onDockedChanged(docked);
    } catch (error) {
      this.#log(`Reporting the dock state failed: ${errorMessage(error)}`);
    }
  }

  #liveWindow(): DockWindow | null {
    const win = this.#options.getWindow();
    return win !== null && !win.isDestroyed() ? win : null;
  }

  #toDip(physical: Rect): Rect {
    const { screen } = this.#options;
    return roundRect(screen.screenToDipRect?.(null, physical) ?? physical);
  }

  #toPhysical(dip: Rect): Rect {
    const { screen } = this.#options;
    return roundRect(screen.dipToScreenRect?.(null, dip) ?? dip);
  }

  /** The CAD window's outer bounds (physical pixels) for a layout's visible CAD rectangle. */
  #cadOuterBounds(layout: DockLayout): Rect {
    return this.#toPhysical(outerBoundsForVisible(layout.cad, this.#options.cadFrameInset ?? 0));
  }

  /** Moves the copilot window into the panel column (leaving any maximised / minimised state). */
  #placeWindow(win: DockWindow, bounds: Rect): void {
    if (win.isMinimized()) win.restore();
    if (win.isFullScreen()) win.setFullScreen(false);
    if (win.isMaximized()) win.unmaximize();
    win.setBounds(bounds);
  }

  async #dock(): Promise<ActionResult> {
    const connection = this.#options.getConnection();
    if (connection === null) return { ok: false, message: NOT_CONNECTED_MESSAGE };
    const win = this.#liveWindow();
    if (win === null) return { ok: false, message: NO_COPILOT_WINDOW_MESSAGE };

    // Docked to a connection that has been replaced: that dock is void.
    if (this.#state !== null && this.#state.connection !== connection) this.#state = null;
    const existing = this.#state;

    try {
      let workAreaSource: Rect;
      let cadPreviousBounds: WindowBounds;
      if (existing !== null) {
        // Re-fit: stay on the display the docked pair is on.
        workAreaSource = existing.layout.cad;
        cadPreviousBounds = existing.cadPreviousBounds;
      } else {
        const physical = await withTimeout(connection.getWindowBounds(), this.#requestTimeoutMs, 'window.getBounds');
        if (physical === null) return { ok: false, message: NO_CAD_WINDOW_MESSAGE };
        cadPreviousBounds = roundRect(physical);
        workAreaSource = this.#toDip(cadPreviousBounds);
      }

      const { workArea } = this.#options.screen.getDisplayMatching(workAreaSource);
      const layout = computeDockLayout(workArea, this.#options.getPanelWidth());
      if (layout === null) return { ok: false, message: DISPLAY_TOO_NARROW_MESSAGE };

      const liveWindow = this.#liveWindow();
      if (liveWindow === null) return { ok: false, message: NO_COPILOT_WINDOW_MESSAGE };
      const panelPreviousBounds = existing?.panelPreviousBounds ?? roundRect(liveWindow.getBounds());

      await withTimeout(
        connection.setWindowBounds(this.#cadOuterBounds(layout)),
        this.#requestTimeoutMs,
        'window.setBounds',
      );
      // The connection may have closed (forget()) or been replaced while the CAD was answering.
      if (this.#options.getConnection() !== connection) {
        return { ok: false, message: 'The CAD application disconnected while docking.' };
      }
      const target = this.#liveWindow();
      if (target === null) return { ok: false, message: NO_COPILOT_WINDOW_MESSAGE };

      this.#state = { connection, cadPreviousBounds, panelPreviousBounds, layout };
      this.#placeWindow(target, layout.panel);
      if (existing === null) this.#notify(true);
      return { ok: true };
    } catch (error) {
      return { ok: false, message: `Docking failed: ${errorMessage(error)}` };
    }
  }

  async #undock(): Promise<ActionResult> {
    const state = this.#state;
    if (state === null) return { ok: true };
    this.#state = null;

    let result: ActionResult = { ok: true };
    try {
      await withTimeout(
        state.connection.setWindowBounds(state.cadPreviousBounds),
        this.#restoreTimeoutMs,
        'window.setBounds',
      );
    } catch (error) {
      // Best effort: the dock is released either way.
      const message = `The CAD window could not be restored: ${errorMessage(error)}`;
      this.#log(message);
      result = { ok: false, message };
    }

    const win = this.#liveWindow();
    if (win !== null) {
      try {
        // Only when the window is still where docking put it; a window the user moved stays put.
        if (sameRect(roundRect(win.getBounds()), state.layout.panel)) {
          // Back to where it was, at the panel width of the settings (it may have changed while
          // docked), keeping the right edge in place.
          const previous = state.panelPreviousBounds;
          const width = Math.min(PANEL_WIDTH_MAX, Math.max(PANEL_WIDTH_MIN, Math.round(this.#options.getPanelWidth())));
          win.setBounds({ x: previous.x + previous.width - width, y: previous.y, width, height: previous.height });
        }
      } catch (error) {
        this.#log(`The CAD Copilot window could not be restored: ${errorMessage(error)}`);
      }
    }
    this.#notify(false);
    return result;
  }

  async #windowResized(): Promise<void> {
    const state = this.#state;
    if (state === null) return;
    const win = this.#liveWindow();
    if (win === null || win.isMinimized() || win.isMaximized() || win.isFullScreen()) return;

    const bounds = roundRect(win.getBounds());
    // Moves and height changes do not change the split; neither do our own setBounds calls
    // (which may land one DIP off on fractionally scaled displays).
    if (Math.abs(bounds.width - state.layout.panelWidth) <= RESIZE_TOLERANCE) return;

    try {
      const { workArea } = this.#options.screen.getDisplayMatching(state.layout.cad);
      const layout = computeDockLayout(workArea, bounds.width);
      if (layout === null) return;
      // Remember what the user chose, unless the display forced a narrower panel than asked for.
      if (layout.panelWidth === dockedPanelWidth(bounds.width, Math.round(workArea.width))) {
        this.#options.setPanelWidth(layout.panelWidth);
      }
      state.layout = layout;
      win.setBounds(layout.panel);
      await withTimeout(
        state.connection.setWindowBounds(this.#cadOuterBounds(layout)),
        this.#requestTimeoutMs,
        'window.setBounds',
      );
    } catch (error) {
      this.#log(`Re-fitting the CAD window failed: ${errorMessage(error)}`);
    }
  }
}
