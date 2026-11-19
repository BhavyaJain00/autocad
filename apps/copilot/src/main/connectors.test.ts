import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  BRIDGE_PROTOCOL_VERSION,
  type ConnectorAdvert,
  type DocumentInfo,
  type HelloResult,
  type ToolCallParams,
  type ToolResult,
  type WindowBounds,
} from '@cadstudio/cad-protocol';
import type { CadConnection } from '@cadstudio/cad-protocol/node';
import type { CadToolExecutor } from '@cadstudio/copilot-agent';
import type { ConnectionState, ConnectorSummary } from '../shared/bridge';
import {
  chooseAutoConnectTarget,
  CONNECT_SUPERSEDED_MESSAGE,
  CONNECTOR_GONE_MESSAGE,
  ConnectorManager,
  toConnectorSummary,
  type ConnectorManagerOptions,
} from './connectors';

const DOCUMENT: DocumentInfo = { id: 'doc-1', name: 'Plan', path: null, unit: 'mm', revision: 1, entityCount: 0 };

function advert(id: string, overrides: Partial<ConnectorAdvert> = {}): ConnectorAdvert {
  const pid = Number(id.split('-')[1] ?? 1);
  return {
    id,
    cad: 'cadstudio',
    displayName: 'CAD Studio',
    cadVersion: '0.1.0',
    connectorVersion: '0.1.0',
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    pipeName: `\\\\.\\pipe\\cadcopilot-${id}`,
    token: 'a'.repeat(64),
    pid,
    startedAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  };
}

function helloResult(overrides: Partial<HelloResult> = {}): HelloResult {
  return {
    cad: 'cadstudio',
    cadVersion: '0.1.0',
    connectorVersion: '0.1.0',
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    document: DOCUMENT,
    supportedTools: ['get_drawing_info', 'create_line'],
    ...overrides,
  };
}

class FakeConnection implements CadConnection {
  readonly advert: ConnectorAdvert;
  helloAnswer: HelloResult = helloResult();
  helloError: Error | null = null;
  helloCalls = 0;
  closed = false;
  readonly toolCalls: ToolCallParams[] = [];
  readonly #notificationListeners = new Set<(method: string, params: unknown) => void>();
  readonly #closeListeners = new Set<(reason: string) => void>();

  constructor(source: ConnectorAdvert) {
    this.advert = source;
  }
  async hello(): Promise<HelloResult> {
    this.helloCalls += 1;
    if (this.helloError !== null) throw this.helloError;
    return structuredClone(this.helloAnswer);
  }
  async callTool(params: ToolCallParams): Promise<ToolResult> {
    this.toolCalls.push(params);
    return { success: true, createdIds: [], modifiedIds: [], deletedIds: [], warnings: [], errors: [], revision: 2 };
  }
  async getWindowBounds(): Promise<WindowBounds | null> {
    return null;
  }
  async setWindowBounds(): Promise<void> {}
  async focusWindow(): Promise<void> {}
  onNotification(listener: (method: string, params: unknown) => void): () => void {
    this.#notificationListeners.add(listener);
    return () => this.#notificationListeners.delete(listener);
  }
  onClose(listener: (reason: string) => void): () => void {
    this.#closeListeners.add(listener);
    return () => this.#closeListeners.delete(listener);
  }
  close(): void {
    this.drop('closed by client');
  }
  /** Simulates a notification from the connector. */
  notify(method: string, params: unknown = {}): void {
    for (const listener of [...this.#notificationListeners]) listener(method, params);
  }
  /** Simulates the connection ending (as the real transport does: listeners run once). */
  drop(reason: string): void {
    if (this.closed) return;
    this.closed = true;
    const listeners = [...this.#closeListeners];
    this.#closeListeners.clear();
    this.#notificationListeners.clear();
    for (const listener of listeners) listener(reason);
  }
}

interface Harness {
  manager: ConnectorManager;
  adverts: ConnectorAdvert[];
  connections: FakeConnection[];
  connectorEvents: ConnectorSummary[][];
  states: ConnectionState[];
  executors: (CadToolExecutor | null)[];
  hooks: string[];
  logs: string[];
  settings: { autoConnect: boolean };
  /** Replaces how connections are made (defaults to a FakeConnection per advert). */
  connectImpl: { fn: (source: ConnectorAdvert) => Promise<CadConnection> };
  listError: { value: Error | null };
}

function createHarness(options: Partial<ConnectorManagerOptions> & { autoConnect?: boolean } = {}): Harness {
  const adverts: ConnectorAdvert[] = [];
  const connections: FakeConnection[] = [];
  const connectorEvents: ConnectorSummary[][] = [];
  const states: ConnectionState[] = [];
  const executors: (CadToolExecutor | null)[] = [];
  const hooks: string[] = [];
  const logs: string[] = [];
  const settings = { autoConnect: options.autoConnect ?? false };
  const listError: { value: Error | null } = { value: null };
  const connectImpl: Harness['connectImpl'] = {
    fn: async (source) => {
      const connection = new FakeConnection(source);
      connections.push(connection);
      return connection;
    },
  };
  const manager = new ConnectorManager({
    listAdverts: async () => {
      if (listError.value !== null) throw listError.value;
      return adverts.map((item) => ({ ...item }));
    },
    connect: (source) => connectImpl.fn(source),
    autoConnectEnabled: () => settings.autoConnect,
    onConnectors: (list) => connectorEvents.push(list),
    onConnection: (state) => states.push(state),
    onExecutor: (executor) => executors.push(executor),
    afterConnected: async () => {
      hooks.push('afterConnected');
    },
    beforeDisconnect: async () => {
      hooks.push('beforeDisconnect');
    },
    onConnectionEnded: () => {
      hooks.push('ended');
    },
    log: (message) => logs.push(message),
    pollIntervalMs: 2000,
    refreshDebounceMs: 150,
    ...options,
  });
  return { manager, adverts, connections, connectorEvents, states, executors, hooks, logs, settings, connectImpl, listError };
}

const statuses = (states: ConnectionState[]): string[] => states.map((state) => state.status);

describe('toConnectorSummary', () => {
  it('exposes only what the UI needs (never the pipe or the token)', () => {
    const summary = toConnectorSummary(advert('cadstudio-41'));
    expect(summary).toEqual({ id: 'cadstudio-41', cad: 'cadstudio', displayName: 'CAD Studio', cadVersion: '0.1.0', pid: 41 });
    expect(JSON.stringify(summary)).not.toContain('pipe');
    expect(JSON.stringify(summary)).not.toContain('a'.repeat(64));
  });
});

describe('chooseAutoConnectTarget', () => {
  const none: ReadonlySet<string> = new Set();

  it('chooses the only connector', () => {
    expect(chooseAutoConnectTarget(['a-1'], null, none)).toBe('a-1');
  });

  it('chooses nothing when there are none or several', () => {
    expect(chooseAutoConnectTarget([], null, none)).toBeNull();
    expect(chooseAutoConnectTarget(['a-1', 'b-2'], null, none)).toBeNull();
  });

  it('prefers the requested connector among several', () => {
    expect(chooseAutoConnectTarget(['a-1', 'b-2'], 'b-2', none)).toBe('b-2');
  });

  it('falls back to the only connector when the requested one is not listed', () => {
    expect(chooseAutoConnectTarget(['a-1'], 'zz-9', none)).toBe('a-1');
    expect(chooseAutoConnectTarget(['a-1', 'b-2'], 'zz-9', none)).toBeNull();
  });

  it('never chooses a suppressed connector', () => {
    expect(chooseAutoConnectTarget(['a-1'], null, new Set(['a-1']))).toBeNull();
    expect(chooseAutoConnectTarget(['a-1', 'b-2'], 'b-2', new Set(['b-2']))).toBeNull();
    expect(chooseAutoConnectTarget(['a-1'], 'b-2', new Set(['b-2']))).toBe('a-1');
  });
});

describe('ConnectorManager', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe('discovery', () => {
    it('reports the list only when it changes', async () => {
      const h = createHarness();
      expect(await h.manager.refresh()).toEqual([]);
      expect(h.connectorEvents).toEqual([]);

      h.adverts.push(advert('cadstudio-1'));
      const listed = await h.manager.refresh();
      expect(listed.map((item) => item.id)).toEqual(['cadstudio-1']);
      expect(h.connectorEvents).toHaveLength(1);

      await h.manager.refresh();
      expect(h.connectorEvents).toHaveLength(1);

      h.adverts.push(advert('autocad-2', { cad: 'autocad', displayName: 'AutoCAD 2026' }));
      await h.manager.refresh();
      expect(h.connectorEvents).toHaveLength(2);
      expect(h.connectorEvents[1]?.map((item) => item.displayName)).toEqual(['CAD Studio', 'AutoCAD 2026']);

      h.adverts.length = 0;
      await h.manager.refresh();
      expect(h.connectorEvents.at(-1)).toEqual([]);
      expect(h.manager.connectors).toEqual([]);
    });

    it('polls every two seconds once started and stops polling when stopped', async () => {
      const h = createHarness();
      const list = vi.fn(async () => h.adverts.map((item) => ({ ...item })));
      const manager = new ConnectorManager({
        listAdverts: list,
        connect: h.connectImpl.fn,
        autoConnectEnabled: () => false,
        onConnectors: (connectors) => h.connectorEvents.push(connectors),
        onConnection: () => undefined,
        onExecutor: () => undefined,
        pollIntervalMs: 2000,
      });
      manager.start();
      await vi.advanceTimersByTimeAsync(0);
      expect(list).toHaveBeenCalledTimes(1);

      h.adverts.push(advert('cadstudio-7'));
      await vi.advanceTimersByTimeAsync(1999);
      expect(list).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(1);
      expect(list).toHaveBeenCalledTimes(2);
      expect(h.connectorEvents.at(-1)?.map((item) => item.id)).toEqual(['cadstudio-7']);

      await vi.advanceTimersByTimeAsync(4000);
      expect(list).toHaveBeenCalledTimes(4);

      await manager.stop();
      await vi.advanceTimersByTimeAsync(10_000);
      expect(list).toHaveBeenCalledTimes(4);
    });

    it('keeps the previous list when the folder cannot be read', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.refresh();
      h.listError.value = new Error('EACCES');
      expect((await h.manager.refresh()).map((item) => item.id)).toEqual(['cadstudio-1']);
      expect(h.connectorEvents).toHaveLength(1);
      expect(h.logs.some((line) => line.includes('EACCES'))).toBe(true);
    });

    it('hands out copies of the list', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      const listed = await h.manager.refresh();
      (listed[0] as ConnectorSummary).displayName = 'changed';
      expect(h.manager.connectors[0]?.displayName).toBe('CAD Studio');
    });
  });

  describe('connect', () => {
    it('goes through connecting to connected with the document and tools from hello', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.refresh();

      expect(await h.manager.connect('cadstudio-1')).toEqual({ ok: true });
      expect(h.states).toEqual([
        { status: 'connecting', connectorId: 'cadstudio-1' },
        {
          status: 'connected',
          connector: { id: 'cadstudio-1', cad: 'cadstudio', displayName: 'CAD Studio', cadVersion: '0.1.0', pid: 1 },
          document: DOCUMENT,
          supportedTools: ['get_drawing_info', 'create_line'],
          docked: false,
        },
      ]);
      expect(h.manager.state.status).toBe('connected');
      expect(h.manager.connection).toBe(h.connections[0]);
      expect(h.hooks).toEqual(['afterConnected']);
    });

    it('gives the session an executor backed by the connection', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      expect(h.executors).toHaveLength(1);
      const executor = h.executors[0];
      expect(executor?.cadName()).toBe('CAD Studio');
      expect(executor?.supportedTools()).toEqual(['get_drawing_info', 'create_line']);
      expect(executor?.currentDocument()).toEqual(DOCUMENT);
      await executor?.execute('create_line', { a: 1 }, { operationId: 'op', instruction: 'go', signal: new AbortController().signal });
      expect(h.connections[0]?.toolCalls[0]).toMatchObject({ name: 'create_line', source: 'ai', operationId: 'op', instruction: 'go' });
    });

    it('finds a connector that appeared since the last poll', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-5'));
      expect(await h.manager.connect('cadstudio-5')).toEqual({ ok: true });
      expect(h.connectorEvents).toHaveLength(1);
    });

    it('fails for a connector that is not running', async () => {
      const h = createHarness();
      expect(await h.manager.connect('cadstudio-404')).toEqual({ ok: false, message: CONNECTOR_GONE_MESSAGE });
      expect(h.states).toEqual([{ status: 'error', message: CONNECTOR_GONE_MESSAGE }]);
      expect(h.connections).toEqual([]);
    });

    it('keeps the current connection when asked for a connector that is not running', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      const before = h.states.length;
      expect((await h.manager.connect('cadstudio-404')).ok).toBe(false);
      expect(h.states).toHaveLength(before);
      expect(h.manager.state.status).toBe('connected');
      expect(h.connections[0]?.closed).toBe(false);
    });

    it('reports a failed connection as an error state with the reason', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      h.connectImpl.fn = async () => {
        throw new Error('CAD Studio refused the connection: bad token');
      };
      const result = await h.manager.connect('cadstudio-1');
      expect(result).toEqual({ ok: false, message: 'CAD Studio refused the connection: bad token' });
      expect(statuses(h.states)).toEqual(['connecting', 'error']);
      expect(h.executors).toEqual([]);
      expect(h.manager.connection).toBeNull();
    });

    it('closes the connection and fails when hello fails', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      h.connectImpl.fn = async (source) => {
        const connection = new FakeConnection(source);
        connection.helloError = new Error('CAD Studio did not answer hello within 30000 ms');
        h.connections.push(connection);
        return connection;
      };
      const result = await h.manager.connect('cadstudio-1');
      expect(result.ok).toBe(false);
      expect(h.connections[0]?.closed).toBe(true);
      expect(statuses(h.states)).toEqual(['connecting', 'error']);
    });

    it('refuses a connector that speaks another protocol version', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      h.connectImpl.fn = async (source) => {
        const connection = new FakeConnection(source);
        connection.helloAnswer = helloResult({ protocolVersion: BRIDGE_PROTOCOL_VERSION + 1 });
        h.connections.push(connection);
        return connection;
      };
      const result = await h.manager.connect('cadstudio-1');
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.message).toContain('protocol version');
      expect(h.connections[0]?.closed).toBe(true);
    });

    it('is a no-op when already connected to that connector', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      const before = h.states.length;
      expect(await h.manager.connect('cadstudio-1')).toEqual({ ok: true });
      expect(h.states).toHaveLength(before);
      expect(h.connections).toHaveLength(1);
    });

    it('joins a connect that is already in flight for the same connector', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.refresh();
      const [first, second] = await Promise.all([h.manager.connect('cadstudio-1'), h.manager.connect('cadstudio-1')]);
      expect(first).toEqual({ ok: true });
      expect(second).toEqual({ ok: true });
      expect(h.connections).toHaveLength(1);
    });

    it('switches CADs: restores and closes the first, then connects the second', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'), advert('autocad-2', { cad: 'autocad', displayName: 'AutoCAD 2026' }));
      await h.manager.connect('cadstudio-1');
      expect(await h.manager.connect('autocad-2')).toEqual({ ok: true });
      expect(h.connections[0]?.closed).toBe(true);
      expect(h.hooks).toEqual(['afterConnected', 'beforeDisconnect', 'ended', 'afterConnected']);
      expect(statuses(h.states)).toEqual(['connecting', 'connected', 'disconnected', 'connecting', 'connected']);
      expect(h.executors.map((executor) => executor?.cadName() ?? null)).toEqual(['CAD Studio', null, 'AutoCAD 2026']);
      expect(h.manager.connection).toBe(h.connections[1]);
    });

    it('a newer request supersedes one that is still connecting', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'), advert('cadstudio-2'));
      await h.manager.refresh();
      let releaseFirst: (connection: CadConnection) => void = () => undefined;
      const firstConnection = new FakeConnection(advert('cadstudio-1'));
      h.connectImpl.fn = (source) => {
        if (source.id === 'cadstudio-1') {
          return new Promise<CadConnection>((resolve) => {
            releaseFirst = resolve;
          });
        }
        const connection = new FakeConnection(source);
        h.connections.push(connection);
        return Promise.resolve(connection);
      };

      const first = h.manager.connect('cadstudio-1');
      await vi.advanceTimersByTimeAsync(0);
      const second = await h.manager.connect('cadstudio-2');
      expect(second).toEqual({ ok: true });

      releaseFirst(firstConnection);
      expect(await first).toEqual({ ok: false, message: CONNECT_SUPERSEDED_MESSAGE });
      expect(firstConnection.closed).toBe(true);
      expect(h.manager.connection).toBe(h.connections[0]);
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-2' } });
    });

    it('a failing afterConnected step does not fail the connection', async () => {
      const h = createHarness({
        afterConnected: async () => {
          throw new Error('dock failed');
        },
      });
      h.adverts.push(advert('cadstudio-1'));
      expect(await h.manager.connect('cadstudio-1')).toEqual({ ok: true });
      expect(h.manager.state.status).toBe('connected');
      expect(h.logs.some((line) => line.includes('dock failed'))).toBe(true);
    });
  });

  describe('document refresh', () => {
    async function connected(): Promise<Harness & { connection: FakeConnection }> {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      const connection = h.connections[0];
      if (connection === undefined) throw new Error('no connection');
      return { ...h, connection };
    }

    it('refreshes through hello 150 ms after document notifications and pushes the new state', async () => {
      const h = await connected();
      const statesBefore = h.states.length;
      h.connection.helloAnswer = helloResult({ document: { ...DOCUMENT, revision: 2, entityCount: 1 } });

      h.connection.notify('document.changed', { documentId: 'doc-1', revision: 2 });
      await vi.advanceTimersByTimeAsync(149);
      expect(h.connection.helloCalls).toBe(1);
      await vi.advanceTimersByTimeAsync(1);
      expect(h.connection.helloCalls).toBe(2);
      expect(h.states).toHaveLength(statesBefore + 1);
      expect(h.states.at(-1)).toMatchObject({ status: 'connected', document: { revision: 2, entityCount: 1 } });
      // The executor sees the refreshed drawing too.
      expect(h.executors[0]?.currentDocument()).toMatchObject({ revision: 2, entityCount: 1 });
    });

    it('debounces a burst of notifications into one hello', async () => {
      const h = await connected();
      for (let index = 0; index < 5; index += 1) {
        h.connection.notify('document.changed', { documentId: 'doc-1', revision: index + 2 });
        await vi.advanceTimersByTimeAsync(100);
      }
      expect(h.connection.helloCalls).toBe(1);
      await vi.advanceTimersByTimeAsync(150);
      expect(h.connection.helloCalls).toBe(2);
    });

    it('refreshes when another drawing becomes active, including no drawing at all', async () => {
      const h = await connected();
      h.connection.helloAnswer = helloResult({ document: null, supportedTools: ['get_drawing_info'] });
      h.connection.notify('document.activated', { document: null });
      await vi.advanceTimersByTimeAsync(150);
      expect(h.states.at(-1)).toMatchObject({ status: 'connected', document: null, supportedTools: ['get_drawing_info'] });
    });

    it('ignores selection changes and unknown notifications', async () => {
      const h = await connected();
      h.connection.notify('selection.changed', { ids: ['e1'] });
      h.connection.notify('something.else');
      await vi.advanceTimersByTimeAsync(1000);
      expect(h.connection.helloCalls).toBe(1);
    });

    it('does not push a state that did not change', async () => {
      const h = await connected();
      const before = h.states.length;
      h.connection.notify('document.changed', { documentId: 'doc-1', revision: 1 });
      await vi.advanceTimersByTimeAsync(150);
      expect(h.connection.helloCalls).toBe(2);
      expect(h.states).toHaveLength(before);
    });

    it('keeps the last known state when a refresh fails', async () => {
      const h = await connected();
      const before = h.states.length;
      h.connection.helloError = new Error('timed out');
      h.connection.notify('document.changed', { documentId: 'doc-1', revision: 2 });
      await vi.advanceTimersByTimeAsync(150);
      expect(h.states).toHaveLength(before);
      expect(h.manager.state.status).toBe('connected');
      expect(h.logs.some((line) => line.includes('timed out'))).toBe(true);
    });

    it('drops a pending refresh when the connection ends', async () => {
      const h = await connected();
      h.connection.notify('document.changed', { documentId: 'doc-1', revision: 2 });
      h.connection.drop('connection lost');
      await vi.advanceTimersByTimeAsync(1000);
      expect(h.connection.helloCalls).toBe(1);
      expect(h.manager.state).toEqual({ status: 'disconnected' });
    });
  });

  describe('disconnect and connection loss', () => {
    it('disconnect restores the CAD window first, closes the connection and clears the executor', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      await h.manager.disconnect();
      expect(h.hooks).toEqual(['afterConnected', 'beforeDisconnect', 'ended']);
      expect(h.connections[0]?.closed).toBe(true);
      expect(h.executors.at(-1)).toBeNull();
      expect(h.states.at(-1)).toEqual({ status: 'disconnected' });
      expect(h.manager.connection).toBeNull();
    });

    it('a lost connection becomes disconnected without the restore step', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      h.connections[0]?.drop('connection lost');
      expect(h.hooks).toEqual(['afterConnected', 'ended']);
      expect(h.executors).toHaveLength(2);
      expect(h.executors[1]).toBeNull();
      expect(statuses(h.states)).toEqual(['connecting', 'connected', 'disconnected']);
    });

    it('reports the end of a connection only once', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      await h.manager.disconnect();
      await h.manager.disconnect();
      expect(h.hooks.filter((hook) => hook === 'ended')).toHaveLength(1);
      expect(statuses(h.states)).toEqual(['connecting', 'connected', 'disconnected']);
    });

    it('disconnect while connecting cancels the attempt', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.refresh();
      let release: (connection: CadConnection) => void = () => undefined;
      const pendingConnection = new FakeConnection(advert('cadstudio-1'));
      h.connectImpl.fn = () =>
        new Promise<CadConnection>((resolve) => {
          release = resolve;
        });
      const attempt = h.manager.connect('cadstudio-1');
      await vi.advanceTimersByTimeAsync(0);
      expect(h.manager.state.status).toBe('connecting');

      await h.manager.disconnect();
      expect(h.manager.state).toEqual({ status: 'disconnected' });
      release(pendingConnection);
      expect(await attempt).toEqual({ ok: false, message: CONNECT_SUPERSEDED_MESSAGE });
      expect(pendingConnection.closed).toBe(true);
      expect(h.manager.state).toEqual({ status: 'disconnected' });
      expect(h.executors).toEqual([]);
    });

    it('disconnect clears an error state', async () => {
      const h = createHarness();
      await h.manager.connect('cadstudio-404');
      await h.manager.disconnect();
      expect(h.manager.state).toEqual({ status: 'disconnected' });
    });

    it('stop closes the connection after the restore step', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      await h.manager.stop();
      expect(h.hooks).toEqual(['afterConnected', 'beforeDisconnect', 'ended']);
      expect(h.connections[0]?.closed).toBe(true);
    });

    it('a failing listener does not break the connection handling', async () => {
      const h = createHarness({
        onConnection: () => {
          throw new Error('renderer gone');
        },
      });
      h.adverts.push(advert('cadstudio-1'));
      expect(await h.manager.connect('cadstudio-1')).toEqual({ ok: true });
      expect(h.manager.state.status).toBe('connected');
    });
  });

  describe('docked flag', () => {
    it('is part of the connected state and resets with the connection', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.connect('cadstudio-1');
      h.manager.setDocked(true);
      expect(h.states.at(-1)).toMatchObject({ status: 'connected', docked: true });
      const count = h.states.length;
      h.manager.setDocked(true);
      expect(h.states).toHaveLength(count);
      h.manager.setDocked(false);
      expect(h.states.at(-1)).toMatchObject({ status: 'connected', docked: false });

      h.manager.setDocked(true);
      h.connections[0]?.drop('connection lost');
      await h.manager.connect('cadstudio-1');
      expect(h.states.at(-1)).toMatchObject({ status: 'connected', docked: false });
    });

    it('is ignored while not connected', () => {
      const h = createHarness();
      h.manager.setDocked(true);
      expect(h.states).toEqual([]);
    });
  });

  describe('auto-connect', () => {
    it('connects to the only connector once started', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-1' } });
      await h.manager.stop();
    });

    it('does nothing when the setting is off', async () => {
      const h = createHarness({ autoConnect: false });
      h.adverts.push(advert('cadstudio-1'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(5000);
      expect(h.states).toEqual([]);
      await h.manager.stop();
    });

    it('does not choose between several connectors', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'), advert('cadstudio-2'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(5000);
      expect(h.states).toEqual([]);
      await h.manager.stop();
    });

    it('prefers the connector named by CADCOPILOT_CONNECT among several', async () => {
      const h = createHarness({ autoConnect: true, preferredConnectorId: 'cadstudio-2' });
      h.adverts.push(advert('cadstudio-1'), advert('cadstudio-2'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-2' } });
      await h.manager.stop();
    });

    it('connects when a CAD application starts later', async () => {
      const h = createHarness({ autoConnect: true });
      h.manager.start();
      await vi.advanceTimersByTimeAsync(2000);
      expect(h.states).toEqual([]);
      h.adverts.push(advert('cadstudio-9'));
      await vi.advanceTimersByTimeAsync(2000);
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-9' } });
      await h.manager.stop();
    });

    it('does not reconnect to a connector the user disconnected from, until it restarts', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      await h.manager.disconnect();
      await vi.advanceTimersByTimeAsync(6000);
      expect(h.manager.state).toEqual({ status: 'disconnected' });
      expect(h.connections).toHaveLength(1);

      // The CAD closes; a new instance (new advert id) is connected to again.
      h.adverts.length = 0;
      await vi.advanceTimersByTimeAsync(2000);
      h.adverts.push(advert('cadstudio-2'));
      await vi.advanceTimersByTimeAsync(2000);
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-2' } });
      await h.manager.stop();
    });

    it('a manual connect still works for a connector the user disconnected from', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      await h.manager.disconnect();
      expect(await h.manager.connect('cadstudio-1')).toEqual({ ok: true });
      await h.manager.stop();
    });

    it('reconnects after the connection was lost while the CAD is still running', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      h.connections[0]?.drop('connection lost');
      expect(h.manager.state).toEqual({ status: 'disconnected' });
      await vi.advanceTimersByTimeAsync(2000);
      expect(h.manager.state.status).toBe('connected');
      expect(h.connections).toHaveLength(2);
      await h.manager.stop();
    });

    it('does not reconnect to a connector that announced it is closing', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      h.connections[0]?.drop('connector closing');
      // Its advert lingers for a few polls.
      await vi.advanceTimersByTimeAsync(6000);
      expect(h.manager.state).toEqual({ status: 'disconnected' });
      expect(h.connections).toHaveLength(1);
      await h.manager.stop();
    });

    it('a failed automatic attempt is quiet and is not retried', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      const connect = vi.fn(async () => {
        throw new Error('pipe busy');
      });
      h.connectImpl.fn = connect;
      h.manager.start();
      await vi.advanceTimersByTimeAsync(8000);
      expect(connect).toHaveBeenCalledTimes(1);
      expect(statuses(h.states)).toEqual(['connecting', 'disconnected']);
      expect(h.logs.some((line) => line.includes('pipe busy'))).toBe(true);
      await h.manager.stop();
    });

    it('resumeAutoConnect makes skipped connectors candidates again', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      await h.manager.disconnect();
      await h.manager.resumeAutoConnect();
      await vi.advanceTimersByTimeAsync(0);
      expect(h.manager.state.status).toBe('connected');
      await h.manager.stop();
    });

    it('does not connect automatically before start or after stop', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'));
      await h.manager.refresh();
      await vi.advanceTimersByTimeAsync(0);
      expect(h.states).toEqual([]);
    });
  });

  describe('requestConnect (a CAD application asked for the copilot)', () => {
    it('connects to the named connector even when auto-connect is off', async () => {
      const h = createHarness({ autoConnect: false });
      h.adverts.push(advert('cadstudio-1'), advert('cadstudio-2'));
      await h.manager.requestConnect('cadstudio-2');
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-2' } });
    });

    it('switches from another CAD', async () => {
      const h = createHarness();
      h.adverts.push(advert('cadstudio-1'), advert('cadstudio-2'));
      await h.manager.connect('cadstudio-1');
      await h.manager.requestConnect('cadstudio-2');
      expect(h.connections[0]?.closed).toBe(true);
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-2' } });
    });

    it('remembers a connector that is not listed yet and prefers it once it appears', async () => {
      const h = createHarness({ autoConnect: true });
      h.adverts.push(advert('cadstudio-1'), advert('cadstudio-3'));
      h.manager.start();
      await vi.advanceTimersByTimeAsync(0);
      await h.manager.requestConnect('cadstudio-2');
      expect(h.states).toEqual([]);
      h.adverts.push(advert('cadstudio-2'));
      await vi.advanceTimersByTimeAsync(2000);
      expect(h.manager.state).toMatchObject({ status: 'connected', connector: { id: 'cadstudio-2' } });
      await h.manager.stop();
    });
  });
});
