import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DEFAULT_APPROVAL_MODE, DEFAULT_EFFORT, DEFAULT_MODEL } from '@cadstudio/copilot-agent';
import { PANEL_WIDTH_DEFAULT, PANEL_WIDTH_MAX, PANEL_WIDTH_MIN } from '../shared/bridge';
import {
  clampPanelWidth,
  DEFAULT_SETTINGS,
  parseSettingsPatch,
  sanitizeSettings,
  SETTINGS_FILE_VERSION,
  SettingsStore,
} from './settings';

describe('DEFAULT_SETTINGS', () => {
  it('matches the documented defaults', () => {
    expect(DEFAULT_SETTINGS).toEqual({
      model: DEFAULT_MODEL,
      effort: DEFAULT_EFFORT,
      approvalMode: DEFAULT_APPROVAL_MODE,
      panelWidth: PANEL_WIDTH_DEFAULT,
      alwaysOnTop: false,
      autoDock: true,
      autoConnect: true,
    });
  });
});

describe('clampPanelWidth', () => {
  it('rounds and clamps to the panel limits', () => {
    expect(clampPanelWidth(500.4)).toBe(500);
    expect(clampPanelWidth(10)).toBe(PANEL_WIDTH_MIN);
    expect(clampPanelWidth(99_999)).toBe(PANEL_WIDTH_MAX);
    expect(clampPanelWidth(-Infinity)).toBe(PANEL_WIDTH_DEFAULT);
    expect(clampPanelWidth(Number.NaN)).toBe(PANEL_WIDTH_DEFAULT);
  });
});

describe('sanitizeSettings', () => {
  it('returns the defaults for anything that is not an object', () => {
    for (const value of [undefined, null, 42, 'settings', [], true]) {
      expect(sanitizeSettings(value)).toEqual(DEFAULT_SETTINGS);
    }
  });

  it('keeps valid fields', () => {
    const stored = {
      model: 'claude-sonnet-5-5',
      effort: 'low',
      approvalMode: 'ask-all',
      panelWidth: 600,
      alwaysOnTop: true,
      autoDock: false,
      autoConnect: false,
    };
    expect(sanitizeSettings(stored)).toEqual(stored);
  });

  it('replaces each invalid field with its default and keeps the others', () => {
    expect(
      sanitizeSettings({
        model: 'gpt-4',
        effort: 'extreme',
        approvalMode: 'never',
        panelWidth: '600',
        alwaysOnTop: 'yes',
        autoDock: 0,
        autoConnect: false,
      }),
    ).toEqual({ ...DEFAULT_SETTINGS, autoConnect: false });
  });

  it('clamps the panel width and drops unknown fields', () => {
    const result = sanitizeSettings({ panelWidth: 5, hasApiKey: true, testModel: true, apiKey: 'sk-secret' });
    expect(result.panelWidth).toBe(PANEL_WIDTH_MIN);
    expect(Object.keys(result).sort()).toEqual(Object.keys(DEFAULT_SETTINGS).sort());
    expect(sanitizeSettings({ panelWidth: 4000 }).panelWidth).toBe(PANEL_WIDTH_MAX);
    expect(sanitizeSettings({ panelWidth: Number.NaN }).panelWidth).toBe(PANEL_WIDTH_DEFAULT);
  });
});

describe('parseSettingsPatch', () => {
  it('accepts every user-editable setting', () => {
    const patch = {
      model: 'claude-haiku-5-5',
      effort: 'max',
      approvalMode: 'auto',
      panelWidth: 520,
      alwaysOnTop: true,
      autoDock: false,
      autoConnect: false,
    };
    expect(parseSettingsPatch(patch)).toEqual({ ok: true, value: patch });
  });

  it('accepts an empty patch and ignores derived or unknown fields', () => {
    expect(parseSettingsPatch({})).toEqual({ ok: true, value: {} });
    expect(parseSettingsPatch({ hasApiKey: true, testModel: true, other: 1, effort: 'low' })).toEqual({
      ok: true,
      value: { effort: 'low' },
    });
  });

  it('clamps the panel width', () => {
    expect(parseSettingsPatch({ panelWidth: 1 })).toEqual({ ok: true, value: { panelWidth: PANEL_WIDTH_MIN } });
    expect(parseSettingsPatch({ panelWidth: 1e9 })).toEqual({ ok: true, value: { panelWidth: PANEL_WIDTH_MAX } });
  });

  it('rejects values that are not objects', () => {
    for (const value of [undefined, null, 'model', 3, [], [{ model: 'claude-opus-5-5' }]]) {
      expect(parseSettingsPatch(value).ok).toBe(false);
    }
  });

  it('rejects the whole patch when one field is invalid', () => {
    const invalid: unknown[] = [
      { model: 'claude-opus-4' },
      { model: 5 },
      { effort: 'HIGH' },
      { approvalMode: 'ask' },
      { panelWidth: '500' },
      { panelWidth: Number.POSITIVE_INFINITY },
      { panelWidth: Number.NaN },
      { alwaysOnTop: 1 },
      { autoDock: 'true' },
      { autoConnect: null },
      { effort: 'low', alwaysOnTop: 'no' },
    ];
    for (const patch of invalid) {
      const result = parseSettingsPatch(patch);
      expect(result.ok, JSON.stringify(patch)).toBe(false);
    }
  });
});

describe('SettingsStore', () => {
  let directory: string;
  let filePath: string;

  beforeEach(() => {
    directory = mkdtempSync(path.join(os.tmpdir(), 'copilot-settings-'));
    filePath = path.join(directory, 'nested', 'settings.json');
  });

  afterEach(() => {
    rmSync(directory, { recursive: true, force: true });
  });

  it('starts with the defaults when the file is missing, without creating it', () => {
    const logs: string[] = [];
    const store = new SettingsStore(filePath, { log: (message) => logs.push(message) });
    expect(store.load()).toEqual(DEFAULT_SETTINGS);
    expect(logs).toEqual([]);
    expect(readdirSync(directory)).toEqual([]);
  });

  it('persists an update and reads it back in a new store', async () => {
    const store = new SettingsStore(filePath);
    store.load();
    expect(store.update({ effort: 'low', panelWidth: 640, alwaysOnTop: true }).sort()).toEqual([
      'alwaysOnTop',
      'effort',
      'panelWidth',
    ]);
    await store.flush();

    const onDisk = JSON.parse(readFileSync(filePath, 'utf8')) as Record<string, unknown>;
    expect(onDisk).toEqual({ version: SETTINGS_FILE_VERSION, ...DEFAULT_SETTINGS, effort: 'low', panelWidth: 640, alwaysOnTop: true });

    const reopened = new SettingsStore(filePath);
    expect(reopened.load()).toEqual({ ...DEFAULT_SETTINGS, effort: 'low', panelWidth: 640, alwaysOnTop: true });
    // No temp files are left next to the settings.
    expect(readdirSync(path.dirname(filePath))).toEqual(['settings.json']);
  });

  it('reports no change and writes nothing when the values are the same', async () => {
    let writes = 0;
    const store = new SettingsStore(filePath, {
      write: async () => {
        writes += 1;
      },
    });
    store.load();
    expect(store.update({ effort: DEFAULT_EFFORT, autoDock: true })).toEqual([]);
    expect(store.update({})).toEqual([]);
    await store.flush();
    expect(writes).toBe(0);
  });

  it('falls back to the defaults for a damaged file and says why', () => {
    writeFileSync(path.join(directory, 'broken.json'), '{ "model": ');
    const logs: string[] = [];
    const store = new SettingsStore(path.join(directory, 'broken.json'), { log: (message) => logs.push(message) });
    expect(store.load()).toEqual(DEFAULT_SETTINGS);
    expect(logs).toHaveLength(1);
    expect(logs[0]).toContain('using the defaults');
  });

  it('repairs invalid fields of an existing file on load', () => {
    writeFileSync(
      path.join(directory, 'partial.json'),
      JSON.stringify({ model: 'claude-fable-5-2', effort: 'nope', panelWidth: 9000, autoConnect: false }),
    );
    const store = new SettingsStore(path.join(directory, 'partial.json'));
    expect(store.load()).toEqual({
      ...DEFAULT_SETTINGS,
      model: 'claude-fable-5-2',
      panelWidth: PANEL_WIDTH_MAX,
      autoConnect: false,
    });
  });

  it('reads a file with a byte order mark', () => {
    writeFileSync(path.join(directory, 'bom.json'), `﻿${JSON.stringify({ effort: 'medium' })}`);
    expect(new SettingsStore(path.join(directory, 'bom.json')).load().effort).toBe('medium');
  });

  it('returns copies that cannot change the stored settings', () => {
    const store = new SettingsStore(filePath);
    const copy = store.load();
    copy.panelWidth = 1;
    expect(store.get().panelWidth).toBe(PANEL_WIDTH_DEFAULT);
  });

  it('keeps the in-memory settings and logs when saving fails', async () => {
    const logs: string[] = [];
    const store = new SettingsStore(filePath, {
      log: (message) => logs.push(message),
      write: async () => {
        throw new Error('disk full');
      },
    });
    store.load();
    expect(store.update({ autoDock: false })).toEqual(['autoDock']);
    await store.flush();
    expect(store.get().autoDock).toBe(false);
    expect(logs).toEqual(['The settings could not be saved: disk full']);
  });

  it('writes one state at a time and ends with the latest', async () => {
    const written: unknown[] = [];
    let active = 0;
    let maxActive = 0;
    const store = new SettingsStore(filePath, {
      write: async (_file, value) => {
        active += 1;
        maxActive = Math.max(maxActive, active);
        await new Promise((resolve) => setTimeout(resolve, 5));
        written.push(value);
        active -= 1;
      },
    });
    store.load();
    store.update({ panelWidth: 400 });
    store.update({ panelWidth: 500 });
    store.update({ panelWidth: 600 });
    await store.flush();
    expect(maxActive).toBe(1);
    expect(written).toHaveLength(3);
    expect((written.at(-1) as { panelWidth: number }).panelWidth).toBe(600);
  });
});
