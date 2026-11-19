import { describe, it, expect } from 'vitest';
import type {
  ConnectionState,
  ConnectorSummary,
  CopilotSettings,
  CopilotSnapshot,
  TimelineEntry,
} from '../../shared/bridge';
import { INITIAL_APP_STATE, appReducer } from './app-state';
import type { AppState } from './app-state';

const AT = '2026-11-13T10:00:00.000Z';

const SETTINGS: CopilotSettings = {
  model: 'claude-opus-5-5',
  effort: 'high',
  approvalMode: 'ask-destructive',
  panelWidth: 460,
  alwaysOnTop: false,
  autoDock: true,
  autoConnect: true,
  hasApiKey: true,
  testModel: false,
};

const CONNECTOR: ConnectorSummary = { id: 'cadstudio-100', cad: 'cadstudio', displayName: 'CAD Studio', cadVersion: '0.1.0', pid: 100 };

const CONNECTED: ConnectionState = {
  status: 'connected',
  connector: CONNECTOR,
  document: { id: 'd1', name: 'Drawing1', path: null, unit: 'ft', revision: 4, entityCount: 12 },
  supportedTools: ['create_line'],
  docked: true,
};

function userEntry(seq: number, text: string): TimelineEntry {
  return { seq, at: AT, type: 'user.message', text, attachments: [] };
}

function deltaEntry(seq: number, text: string): TimelineEntry {
  return { seq, at: AT, type: 'message.delta', messageId: 'm1', text };
}

function snapshot(overrides: Partial<CopilotSnapshot> = {}): CopilotSnapshot {
  return {
    appVersion: '0.1.0',
    settings: SETTINGS,
    connection: CONNECTED,
    connectors: [CONNECTOR],
    busy: false,
    timeline: [],
    ...overrides,
  };
}

function assistantText(state: AppState): string | undefined {
  const message = state.transcript.items.find((item) => item.kind === 'assistant');
  return message?.kind === 'assistant' ? message.text : undefined;
}

describe('appReducer', () => {
  it('starts in the loading phase without settings', () => {
    expect(INITIAL_APP_STATE.phase).toBe('loading');
    expect(INITIAL_APP_STATE.settings).toBeNull();
    expect(INITIAL_APP_STATE.connection).toEqual({ status: 'disconnected' });
    expect(INITIAL_APP_STATE.busy).toBe(false);
  });

  it('becomes ready with everything the snapshot holds', () => {
    const state = appReducer(
      INITIAL_APP_STATE,
      { type: 'snapshot', snapshot: snapshot({ busy: true, timeline: [userEntry(1, 'hi'), deltaEntry(2, 'Hello')] }) },
    );
    expect(state.phase).toBe('ready');
    expect(state.appVersion).toBe('0.1.0');
    expect(state.settings).toEqual(SETTINGS);
    expect(state.connection).toEqual(CONNECTED);
    expect(state.connectors).toEqual([CONNECTOR]);
    expect(state.busy).toBe(true);
    expect(state.transcript.items).toHaveLength(2);
    expect(assistantText(state)).toBe('Hello');
    expect(state.transcript.lastSeq).toBe(2);
  });

  it('buffers live entries that arrive before the snapshot and replays only the new ones', () => {
    let state = appReducer(INITIAL_APP_STATE, { type: 'timeline', entries: [deltaEntry(2, 'Hello')] });
    state = appReducer(state, { type: 'timeline', entries: [deltaEntry(3, ' world')] });
    expect(state.transcript.items).toHaveLength(0);
    expect(state.earlyEntries).toHaveLength(2);
    // The snapshot already contains seq 2 (coalesced); seq 3 arrived after it was taken.
    state = appReducer(state, { type: 'snapshot', snapshot: snapshot({ timeline: [userEntry(1, 'hi'), deltaEntry(2, 'Hello')] }) });
    expect(assistantText(state)).toBe('Hello world');
    expect(state.earlyEntries).toEqual([]);
    expect(state.transcript.lastSeq).toBe(3);
  });

  it('drops early entries the snapshot already covers', () => {
    let state = appReducer(INITIAL_APP_STATE, { type: 'timeline', entries: [userEntry(1, 'hi'), deltaEntry(2, 'Hello')] });
    state = appReducer(state, { type: 'snapshot', snapshot: snapshot({ timeline: [userEntry(1, 'hi'), deltaEntry(2, 'Hello')] }) });
    expect(state.transcript.items).toHaveLength(2);
    expect(assistantText(state)).toBe('Hello');
  });

  it('applies live entries once ready and ignores duplicates', () => {
    const ready = appReducer(INITIAL_APP_STATE, { type: 'snapshot', snapshot: snapshot({ timeline: [userEntry(1, 'hi')] }) });
    const next = appReducer(ready, { type: 'timeline', entries: [deltaEntry(2, 'A'), deltaEntry(3, 'B')] });
    expect(assistantText(next)).toBe('AB');
    const replay = appReducer(next, { type: 'timeline', entries: [deltaEntry(2, 'A'), deltaEntry(3, 'B')] });
    expect(replay).toBe(next);
    expect(appReducer(next, { type: 'timeline', entries: [] })).toBe(next);
  });

  it('follows connection, connector, settings and busy events', () => {
    const ready = appReducer(INITIAL_APP_STATE, { type: 'snapshot', snapshot: snapshot() });
    const disconnected = appReducer(ready, { type: 'connection', connection: { status: 'disconnected' } });
    expect(disconnected.connection).toEqual({ status: 'disconnected' });
    const noConnectors = appReducer(disconnected, { type: 'connectors', connectors: [] });
    expect(noConnectors.connectors).toEqual([]);
    const sonnet = appReducer(noConnectors, { type: 'settings', settings: { ...SETTINGS, model: 'claude-sonnet-5-5' } });
    expect(sonnet.settings?.model).toBe('claude-sonnet-5-5');
    const busy = appReducer(sonnet, { type: 'busy', busy: true });
    expect(busy.busy).toBe(true);
    expect(appReducer(busy, { type: 'busy', busy: true })).toBe(busy);
    // Nothing else changed along the way.
    expect(busy.transcript).toBe(ready.transcript);
    expect(busy.appVersion).toBe('0.1.0');
  });

  it('reports a failed snapshot, but never downgrades a ready app', () => {
    const failed = appReducer(INITIAL_APP_STATE, { type: 'snapshot-failed', message: 'IPC closed' });
    expect(failed.phase).toBe('failed');
    expect(failed.loadError).toBe('IPC closed');
    const ready = appReducer(INITIAL_APP_STATE, { type: 'snapshot', snapshot: snapshot() });
    expect(appReducer(ready, { type: 'snapshot-failed', message: 'late' })).toBe(ready);
  });

  it('a later snapshot replaces the transcript instead of appending to it', () => {
    const first = appReducer(INITIAL_APP_STATE, { type: 'snapshot', snapshot: snapshot({ timeline: [userEntry(1, 'old')] }) });
    const second = appReducer(first, { type: 'snapshot', snapshot: snapshot({ timeline: [userEntry(9, 'new')] }) });
    expect(second.transcript.items).toHaveLength(1);
    const only = second.transcript.items[0];
    expect(only?.kind === 'user' ? only.text : null).toBe('new');
  });
});
