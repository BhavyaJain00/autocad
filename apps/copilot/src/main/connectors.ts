/**
 * Connector discovery and the connection to one CAD application: polls the adverts folder,
 * connects (manually or automatically), keeps the connector's `hello` state fresh and reports
 * every change. The transport is injected, so this module has no Electron imports and is
 * unit-testable with a fake connection.
 */
import {
  BRIDGE_NOTIFICATIONS,
  BRIDGE_PROTOCOL_VERSION,
  type ConnectorAdvert,
  type HelloResult,
} from '@cadstudio/cad-protocol';
import type { CadConnection } from '@cadstudio/cad-protocol/node';
import type { CadToolExecutor } from '@cadstudio/copilot-agent';
import type { ActionResult, ConnectionState, ConnectorSummary } from '../shared/bridge';
import { CONNECTOR_POLL_INTERVAL_MS, DOCUMENT_REFRESH_DEBOUNCE_MS } from './constants';
import { cadDisplayName, createConnectionExecutor } from './executor';
import { errorMessage } from './json-file';

export const CONNECTOR_GONE_MESSAGE = 'That CAD application is no longer running.';
export const CONNECT_SUPERSEDED_MESSAGE = 'The connection attempt was cancelled.';

export interface ConnectorManagerOptions {
  /** Lists the running connectors (`listAdverts` of @cadstudio/cad-protocol/node). */
  listAdverts(): Promise<ConnectorAdvert[]>;
  /** Opens an authenticated connection (`connectToConnector`). */
  connect(advert: ConnectorAdvert): Promise<CadConnection>;
  /** The autoConnect setting, read whenever the list of connectors is refreshed. */
  autoConnectEnabled(): boolean;
  /** Advert id from CADCOPILOT_CONNECT: the connector to prefer when connecting automatically. */
  readonly preferredConnectorId?: string | null;
  /** The list of running CAD applications changed. */
  onConnectors(connectors: ConnectorSummary[]): void;
  /** The connection state changed. */
  onConnection(state: ConnectionState): void;
  /** The session's executor changed: a new one after connecting, null after the connection ended. */
  onExecutor(executor: CadToolExecutor | null): void;
  /** Runs once a connection is established (auto-dock). Failures are logged, never fatal. */
  afterConnected?(connection: CadConnection): Promise<void>;
  /** Runs before a connection is closed on purpose (restores the CAD window). */
  beforeDisconnect?(connection: CadConnection): Promise<void>;
  /** The connection ended, for whatever reason (the dock state must be forgotten). */
  onConnectionEnded?(connection: CadConnection): void;
  readonly pollIntervalMs?: number;
  readonly refreshDebounceMs?: number;
  readonly log?: (message: string) => void;
}

interface ActiveConnection {
  readonly connection: CadConnection;
  readonly summary: ConnectorSummary;
  hello: HelloResult;
  refreshTimer: ReturnType<typeof setTimeout> | undefined;
}

/** What the UI shows of a connector advert (never its pipe name or token). */
export function toConnectorSummary(advert: ConnectorAdvert): ConnectorSummary {
  return {
    id: advert.id,
    cad: advert.cad,
    displayName: cadDisplayName(advert),
    cadVersion: advert.cadVersion,
    pid: advert.pid,
  };
}

/**
 * Chooses the connector to connect to automatically: the preferred one when it is listed,
 * otherwise the only one listed. Connectors in `suppressed` (the user disconnected from them, or
 * connecting failed) are never chosen. Null when there is no unambiguous choice.
 */
export function chooseAutoConnectTarget(
  connectorIds: readonly string[],
  preferredId: string | null,
  suppressed: ReadonlySet<string>,
): string | null {
  if (preferredId !== null && connectorIds.includes(preferredId) && !suppressed.has(preferredId)) return preferredId;
  const only = connectorIds.length === 1 ? connectorIds[0] : undefined;
  return only !== undefined && !suppressed.has(only) ? only : null;
}

export class ConnectorManager {
  readonly #options: ConnectorManagerOptions;
  readonly #log: (message: string) => void;
  readonly #pollIntervalMs: number;
  readonly #refreshDebounceMs: number;

  #adverts: ConnectorAdvert[] = [];
  #summaries: ConnectorSummary[] = [];
  #state: ConnectionState = { status: 'disconnected' };
  #active: ActiveConnection | null = null;
  #docked = false;
  /** Incremented by every connect / disconnect request; an attempt that is no longer current stops. */
  #attempt = 0;
  /** The connect in flight, so a repeated request for the same connector joins it. */
  #connecting: { id: string; result: Promise<ActionResult> } | null = null;
  /** Connectors that are not connected to automatically (until they disappear from the list). */
  readonly #suppressed = new Set<string>();
  #preferredId: string | null;
  #listing: Promise<void> | null = null;
  #pollTimer: ReturnType<typeof setTimeout> | undefined;
  #running = false;

  constructor(options: ConnectorManagerOptions) {
    this.#options = options;
    this.#log = options.log ?? (() => undefined);
    this.#pollIntervalMs = options.pollIntervalMs ?? CONNECTOR_POLL_INTERVAL_MS;
    this.#refreshDebounceMs = options.refreshDebounceMs ?? DOCUMENT_REFRESH_DEBOUNCE_MS;
    this.#preferredId = options.preferredConnectorId ?? null;
  }

  /** The current connection state (a copy). */
  get state(): ConnectionState {
    return structuredClone(this.#state);
  }

  /** The running CAD applications found by the latest poll (copies). */
  get connectors(): ConnectorSummary[] {
    return this.#summaries.map((summary) => ({ ...summary }));
  }

  /** The open connection, or null. */
  get connection(): CadConnection | null {
    return this.#active?.connection ?? null;
  }

  /** Starts polling (first poll immediately). */
  start(): void {
    if (this.#running) return;
    this.#running = true;
    void this.#poll();
  }

  /** Stops polling and closes the connection (restoring the CAD window first). */
  async stop(): Promise<void> {
    this.#running = false;
    clearTimeout(this.#pollTimer);
    this.#pollTimer = undefined;
    this.#attempt += 1;
    this.#connecting = null;
    await this.#closeActive();
  }

  /** Lists the connectors now, reports changes and connects automatically when that applies. */
  async refresh(): Promise<ConnectorSummary[]> {
    await this.#updateList();
    this.#autoConnect();
    return this.connectors;
  }

  /** Connects to a listed connector on the user's request. */
  connect(connectorId: string): Promise<ActionResult> {
    this.#suppressed.delete(connectorId);
    return this.#connectTo(connectorId, false);
  }

  /**
   * A CAD application asked for this copilot (a second launch carrying CADCOPILOT_CONNECT):
   * connect to it now when it is listed; otherwise prefer it once it appears.
   */
  async requestConnect(connectorId: string): Promise<void> {
    this.#preferredId = connectorId;
    this.#suppressed.delete(connectorId);
    await this.#updateList();
    if (this.#adverts.some((advert) => advert.id === connectorId)) await this.#connectTo(connectorId, false);
  }

  /** Disconnects on the user's request; that connector is not reconnected to automatically. */
  async disconnect(): Promise<void> {
    this.#attempt += 1;
    this.#connecting = null;
    if (this.#state.status === 'connecting') {
      this.#suppressed.add(this.#state.connectorId);
      this.#setState({ status: 'disconnected' });
    } else if (this.#state.status === 'error') {
      this.#setState({ status: 'disconnected' });
    }
    if (this.#active !== null) this.#suppressed.add(this.#active.summary.id);
    await this.#closeActive();
  }

  /**
   * Auto-connect was switched on: connectors skipped so far (after a manual disconnect or a
   * failed attempt) are candidates again, and the list is checked right away.
   */
  async resumeAutoConnect(): Promise<void> {
    this.#suppressed.clear();
    await this.refresh();
  }

  /** Records whether the copilot is docked beside the connected CAD (part of the state). */
  setDocked(docked: boolean): void {
    if (this.#docked === docked) return;
    this.#docked = docked;
    if (this.#active !== null) this.#setState(this.#connectedState(this.#active));
  }

  // -------------------------------------------------------------------------------------------

  async #poll(): Promise<void> {
    try {
      await this.refresh();
    } catch (error) {
      this.#log(`Listing the CAD applications failed: ${errorMessage(error)}`);
    }
    if (!this.#running) return;
    this.#pollTimer = setTimeout(() => void this.#poll(), this.#pollIntervalMs);
  }

  /** Reads the adverts folder (one read at a time) and reports a changed list. */
  #updateList(): Promise<void> {
    this.#listing ??= this.#readAdverts().finally(() => {
      this.#listing = null;
    });
    return this.#listing;
  }

  async #readAdverts(): Promise<void> {
    let adverts: ConnectorAdvert[];
    try {
      adverts = await this.#options.listAdverts();
    } catch (error) {
      // Keep the previous list: a folder that cannot be read right now says nothing about the CADs.
      this.#log(`The connectors folder could not be read: ${errorMessage(error)}`);
      return;
    }
    this.#adverts = adverts;
    const listed = new Set(adverts.map((advert) => advert.id));
    for (const id of [...this.#suppressed]) {
      if (!listed.has(id)) this.#suppressed.delete(id);
    }
    const summaries = adverts.map(toConnectorSummary);
    if (JSON.stringify(summaries) === JSON.stringify(this.#summaries)) return;
    this.#summaries = summaries;
    this.#emit(() => this.#options.onConnectors(this.connectors));
  }

  #autoConnect(): void {
    if (!this.#running || this.#active !== null || this.#state.status === 'connecting') return;
    let enabled = false;
    try {
      enabled = this.#options.autoConnectEnabled();
    } catch (error) {
      this.#log(`Reading the auto-connect setting failed: ${errorMessage(error)}`);
    }
    if (!enabled) return;
    const target = chooseAutoConnectTarget(
      this.#adverts.map((advert) => advert.id),
      this.#preferredId,
      this.#suppressed,
    );
    if (target !== null) void this.#connectTo(target, true);
  }

  #connectTo(connectorId: string, automatic: boolean): Promise<ActionResult> {
    if (this.#active !== null && this.#active.summary.id === connectorId) return Promise.resolve({ ok: true });
    if (this.#connecting !== null && this.#connecting.id === connectorId) return this.#connecting.result;
    const attempt = (this.#attempt += 1);
    const result = this.#runConnect(connectorId, automatic, attempt).finally(() => {
      if (this.#connecting?.result === result) this.#connecting = null;
    });
    this.#connecting = { id: connectorId, result };
    return result;
  }

  async #runConnect(connectorId: string, automatic: boolean, attempt: number): Promise<ActionResult> {
    const superseded = (): boolean => attempt !== this.#attempt;
    const cancelled: ActionResult = { ok: false, message: CONNECT_SUPERSEDED_MESSAGE };

    let advert = this.#adverts.find((candidate) => candidate.id === connectorId);
    if (advert === undefined) {
      await this.#updateList();
      if (superseded()) return cancelled;
      advert = this.#adverts.find((candidate) => candidate.id === connectorId);
    }
    if (advert === undefined) {
      if (this.#active === null) this.#setState({ status: 'error', message: CONNECTOR_GONE_MESSAGE });
      return { ok: false, message: CONNECTOR_GONE_MESSAGE };
    }

    // Switching CADs: leave the current one first (its window is restored).
    await this.#closeActive();
    if (superseded()) return cancelled;

    this.#setState({ status: 'connecting', connectorId });
    let connection: CadConnection | undefined;
    let hello: HelloResult;
    try {
      connection = await this.#options.connect(advert);
      if (superseded()) {
        connection.close();
        return cancelled;
      }
      hello = await connection.hello();
      if (superseded()) {
        connection.close();
        return cancelled;
      }
      if (hello.protocolVersion !== BRIDGE_PROTOCOL_VERSION) {
        throw new Error(
          `${cadDisplayName(advert)} speaks connector protocol version ${hello.protocolVersion}, ` +
            `but this CAD Copilot needs version ${BRIDGE_PROTOCOL_VERSION}. Update both to the same release.`,
        );
      }
    } catch (error) {
      connection?.close();
      if (superseded()) return cancelled;
      const message = errorMessage(error);
      // Not retried automatically; the user can still pick it from the list.
      this.#suppressed.add(connectorId);
      if (automatic) {
        this.#log(`Connecting to ${cadDisplayName(advert)} automatically failed: ${message}`);
        this.#setState({ status: 'disconnected' });
      } else {
        this.#setState({ status: 'error', message });
      }
      return { ok: false, message };
    }

    const active: ActiveConnection = {
      connection,
      summary: toConnectorSummary(advert),
      hello,
      refreshTimer: undefined,
    };
    this.#active = active;
    this.#docked = false;
    connection.onNotification((method) => {
      if (method === BRIDGE_NOTIFICATIONS.documentChanged || method === BRIDGE_NOTIFICATIONS.documentActivated) {
        this.#scheduleHelloRefresh(active);
      }
    });
    connection.onClose((reason) => this.#handleClosed(active, reason));
    this.#emit(() =>
      this.#options.onExecutor(createConnectionExecutor(connection, { latestHello: () => active.hello })),
    );
    this.#setState(this.#connectedState(active));

    if (this.#options.afterConnected !== undefined) {
      try {
        await this.#options.afterConnected(connection);
      } catch (error) {
        this.#log(`The step after connecting failed: ${errorMessage(error)}`);
      }
    }
    return { ok: true };
  }

  #connectedState(active: ActiveConnection): ConnectionState {
    return {
      status: 'connected',
      connector: { ...active.summary },
      document: active.hello.document === null ? null : { ...active.hello.document },
      supportedTools: [...active.hello.supportedTools],
      docked: this.#docked,
    };
  }

  /** Asks the connector for its state again after `refreshDebounceMs` without new notifications. */
  #scheduleHelloRefresh(active: ActiveConnection): void {
    clearTimeout(active.refreshTimer);
    active.refreshTimer = setTimeout(() => {
      active.refreshTimer = undefined;
      void this.#refreshHello(active);
    }, this.#refreshDebounceMs);
  }

  async #refreshHello(active: ActiveConnection): Promise<void> {
    if (this.#active !== active) return;
    try {
      const hello = await active.connection.hello();
      if (this.#active !== active) return;
      active.hello = hello;
      this.#setState(this.#connectedState(active));
    } catch (error) {
      if (this.#active === active) this.#log(`Refreshing the drawing state failed: ${errorMessage(error)}`);
    }
  }

  /** Closes the open connection on purpose (after giving `beforeDisconnect` its chance). */
  async #closeActive(): Promise<void> {
    const active = this.#active;
    if (active === null) return;
    if (this.#options.beforeDisconnect !== undefined) {
      try {
        await this.#options.beforeDisconnect(active.connection);
      } catch (error) {
        this.#log(`The step before disconnecting failed: ${errorMessage(error)}`);
      }
    }
    try {
      active.connection.close();
    } catch (error) {
      this.#log(`Closing the connection failed: ${errorMessage(error)}`);
    }
    this.#handleClosed(active, 'closed by client');
  }

  #handleClosed(active: ActiveConnection, reason: string): void {
    if (this.#active !== active) return;
    clearTimeout(active.refreshTimer);
    active.refreshTimer = undefined;
    this.#active = null;
    this.#docked = false;
    // A connector that announced its own shutdown is not reconnected to while its advert lingers.
    if (reason === 'connector closing') this.#suppressed.add(active.summary.id);
    this.#log(`Disconnected from ${active.summary.displayName} (${reason}).`);
    this.#emit(() => this.#options.onConnectionEnded?.(active.connection));
    this.#emit(() => this.#options.onExecutor(null));
    this.#setState({ status: 'disconnected' });
  }

  #setState(state: ConnectionState): void {
    if (JSON.stringify(state) === JSON.stringify(this.#state)) return;
    this.#state = state;
    this.#emit(() => this.#options.onConnection(this.state));
  }

  /** Runs a callback of the owner; its failure must never break the connection handling. */
  #emit(callback: () => void): void {
    try {
      callback();
    } catch (error) {
      this.#log(`A connection listener failed: ${errorMessage(error)}`);
    }
  }
}
