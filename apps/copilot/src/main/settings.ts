/**
 * User settings: validation and persistence as JSON in `<userData>/settings.json`.
 * No Electron imports, so everything here is unit-testable in Node.
 */
import {
  DEFAULT_APPROVAL_MODE,
  DEFAULT_EFFORT,
  DEFAULT_MODEL,
  EFFORTS,
  MODEL_IDS,
  type ApprovalMode,
  type Effort,
  type ModelId,
} from '@cadstudio/copilot-agent';
import {
  APPROVAL_CHOICES,
  PANEL_WIDTH_DEFAULT,
  PANEL_WIDTH_MAX,
  PANEL_WIDTH_MIN,
  type CopilotSettings,
  type SettingsPatch,
} from '../shared/bridge';
import { errorMessage, readJsonFile, writeJsonFileAtomic } from './json-file';

/** The persisted part of {@link CopilotSettings} (hasApiKey and testModel are derived at runtime). */
export type StoredSettings = Pick<
  CopilotSettings,
  'model' | 'effort' | 'approvalMode' | 'panelWidth' | 'alwaysOnTop' | 'autoDock' | 'autoConnect'
>;

export type SettingKey = keyof StoredSettings;

export type Validated<T> = { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: string };

/** Version of the settings file layout. */
export const SETTINGS_FILE_VERSION = 1;

export const DEFAULT_SETTINGS: Readonly<StoredSettings> = Object.freeze({
  model: DEFAULT_MODEL,
  effort: DEFAULT_EFFORT,
  approvalMode: DEFAULT_APPROVAL_MODE,
  panelWidth: PANEL_WIDTH_DEFAULT,
  alwaysOnTop: false,
  autoDock: true,
  autoConnect: true,
});

const APPROVAL_MODES: readonly ApprovalMode[] = APPROVAL_CHOICES.map((choice) => choice.id);

function isModelId(value: unknown): value is ModelId {
  return typeof value === 'string' && (MODEL_IDS as readonly string[]).includes(value);
}

function isEffort(value: unknown): value is Effort {
  return typeof value === 'string' && (EFFORTS as readonly string[]).includes(value);
}

function isApprovalMode(value: unknown): value is ApprovalMode {
  return typeof value === 'string' && (APPROVAL_MODES as readonly string[]).includes(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Rounds a panel width to whole DIP inside [PANEL_WIDTH_MIN, PANEL_WIDTH_MAX]. */
export function clampPanelWidth(width: number): number {
  if (!Number.isFinite(width)) return PANEL_WIDTH_DEFAULT;
  return Math.min(PANEL_WIDTH_MAX, Math.max(PANEL_WIDTH_MIN, Math.round(width)));
}

/**
 * Settings from untrusted JSON (the settings file): every field that is missing or invalid falls
 * back to its default, the panel width is clamped, unknown fields are dropped.
 */
export function sanitizeSettings(value: unknown): StoredSettings {
  const source = isRecord(value) ? value : {};
  const { panelWidth } = source;
  return {
    model: isModelId(source.model) ? source.model : DEFAULT_SETTINGS.model,
    effort: isEffort(source.effort) ? source.effort : DEFAULT_SETTINGS.effort,
    approvalMode: isApprovalMode(source.approvalMode) ? source.approvalMode : DEFAULT_SETTINGS.approvalMode,
    panelWidth:
      typeof panelWidth === 'number' && Number.isFinite(panelWidth)
        ? clampPanelWidth(panelWidth)
        : DEFAULT_SETTINGS.panelWidth,
    alwaysOnTop: typeof source.alwaysOnTop === 'boolean' ? source.alwaysOnTop : DEFAULT_SETTINGS.alwaysOnTop,
    autoDock: typeof source.autoDock === 'boolean' ? source.autoDock : DEFAULT_SETTINGS.autoDock,
    autoConnect: typeof source.autoConnect === 'boolean' ? source.autoConnect : DEFAULT_SETTINGS.autoConnect,
  };
}

/**
 * Validates a settings patch received over IPC. Fields that are not user-editable settings are
 * ignored; a present field with an invalid value rejects the whole patch (nothing is applied).
 * The panel width is clamped to its limits.
 */
export function parseSettingsPatch(value: unknown): Validated<SettingsPatch> {
  if (!isRecord(value)) return { ok: false, error: 'The settings patch must be an object.' };
  const patch: SettingsPatch = {};
  const invalid = (field: string): Validated<SettingsPatch> => ({
    ok: false,
    error: `Invalid value for the "${field}" setting.`,
  });

  if (value.model !== undefined) {
    if (!isModelId(value.model)) return invalid('model');
    patch.model = value.model;
  }
  if (value.effort !== undefined) {
    if (!isEffort(value.effort)) return invalid('effort');
    patch.effort = value.effort;
  }
  if (value.approvalMode !== undefined) {
    if (!isApprovalMode(value.approvalMode)) return invalid('approvalMode');
    patch.approvalMode = value.approvalMode;
  }
  if (value.panelWidth !== undefined) {
    if (typeof value.panelWidth !== 'number' || !Number.isFinite(value.panelWidth)) return invalid('panelWidth');
    patch.panelWidth = clampPanelWidth(value.panelWidth);
  }
  for (const flag of ['alwaysOnTop', 'autoDock', 'autoConnect'] as const) {
    const flagValue = value[flag];
    if (flagValue === undefined) continue;
    if (typeof flagValue !== 'boolean') return invalid(flag);
    patch[flag] = flagValue;
  }
  return { ok: true, value: patch };
}

export interface SettingsStoreOptions {
  /** Receives problems that do not stop the app (unreadable file, failed write). */
  readonly log?: (message: string) => void;
  /** Replaces the atomic JSON writer (tests). */
  readonly write?: (filePath: string, value: unknown) => Promise<void>;
}

/**
 * The settings of this installation. `load()` once at startup; `update()` applies a validated
 * patch in memory immediately and persists it in the background (writes never overlap).
 */
export class SettingsStore {
  readonly #filePath: string;
  readonly #log: (message: string) => void;
  readonly #write: (filePath: string, value: unknown) => Promise<void>;
  #settings: StoredSettings = { ...DEFAULT_SETTINGS };
  #writeQueue: Promise<void> = Promise.resolve();

  constructor(filePath: string, options: SettingsStoreOptions = {}) {
    this.#filePath = filePath;
    this.#log = options.log ?? (() => undefined);
    this.#write = options.write ?? writeJsonFileAtomic;
  }

  /** Reads the settings file; a missing or damaged file yields the defaults. Never throws. */
  load(): StoredSettings {
    const read = readJsonFile(this.#filePath);
    if (read.status === 'invalid') {
      this.#log(`The settings file could not be read (${read.reason}); using the defaults.`);
    }
    this.#settings = sanitizeSettings(read.status === 'ok' ? read.value : undefined);
    return this.get();
  }

  /** A copy of the current settings. */
  get(): StoredSettings {
    return { ...this.#settings };
  }

  /**
   * Applies a patch (already validated with {@link parseSettingsPatch}) and returns the keys whose
   * value changed. Persists only when something changed.
   */
  update(patch: SettingsPatch): SettingKey[] {
    const next = sanitizeSettings({ ...this.#settings, ...patch });
    const changed = (Object.keys(next) as SettingKey[]).filter((key) => next[key] !== this.#settings[key]);
    if (changed.length === 0) return changed;
    this.#settings = next;
    this.#persist();
    return changed;
  }

  /** Resolves once every write started so far has finished (used before quitting and by tests). */
  flush(): Promise<void> {
    return this.#writeQueue;
  }

  #persist(): void {
    this.#writeQueue = this.#writeQueue.then(async () => {
      // Always the latest state: queued writes of older states would only be overwritten.
      const snapshot = { version: SETTINGS_FILE_VERSION, ...this.#settings };
      try {
        await this.#write(this.#filePath, snapshot);
      } catch (error) {
        this.#log(`The settings could not be saved: ${errorMessage(error)}`);
      }
    });
  }
}
