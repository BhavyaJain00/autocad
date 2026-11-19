// Node-only transport of the CAD Copilot bridge: connector discovery files and the named-pipe
// NDJSON JSON-RPC client and server. Used by apps/copilot (client) and by CAD Studio's main
// process (server). The C# AutoCAD plugin implements the same wire protocol.
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { mkdirSync, unlinkSync } from 'node:fs';
import { readFile, readdir, rename, unlink, writeFile } from 'node:fs/promises';
import { connect, createServer } from 'node:net';
import type { Server, Socket } from 'node:net';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  BRIDGE_PROTOCOL_VERSION,
  BridgeError,
  BridgeErrorCode,
  NdjsonDecoder,
  authParamsSchema,
  connectorAdvertSchema,
  encodeNdjson,
  formatValidationIssues,
  helloResultSchema,
  isBridgeError,
  isRpcFailure,
  isRpcNotification,
  isRpcRequest,
  isRpcSuccess,
  setWindowBoundsParamsSchema,
  toolCallParamsSchema,
  toolResultSchema,
  windowBoundsSchema,
} from './index';
import type {
  CadKind,
  ConnectorAdvert,
  HelloResult,
  RpcMessage,
  ToolCallParams,
  ToolResult,
  WindowBounds,
} from './index';

// ---------------------------------------------------------------------------------------------
// Timings
// ---------------------------------------------------------------------------------------------

/** Default time allowed for connecting and authenticating. */
export const DEFAULT_CONNECT_TIMEOUT_MS = 5_000;
/** Default time allowed for one `tools.call`. */
export const DEFAULT_TOOL_TIMEOUT_MS = 120_000;
/** Default time allowed for `hello` and the window methods. */
export const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;
/** A connection that has not authenticated within this time is dropped by the server. */
export const AUTH_DEADLINE_MS = 10_000;

/** How long a socket gets to finish a graceful close before it is destroyed. */
const CLOSE_GRACE_MS = 1_000;

// ---------------------------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------------------------

function copilotHomeOverride(): string | undefined {
  const home = process.env.CADCOPILOT_HOME;
  return home !== undefined && home.trim().length > 0 ? home : undefined;
}

/**
 * The folder connectors advertise themselves in, created when missing:
 * `$CADCOPILOT_HOME/connectors` when that variable is set (tests, portable setups), otherwise
 * `%APPDATA%/CadCopilot/connectors`, or `~/.config/CadCopilot/connectors` without APPDATA.
 */
export function connectorsDirectory(): string {
  const home = copilotHomeOverride();
  const appData = process.env.APPDATA;
  let directory: string;
  if (home !== undefined) directory = join(home, 'connectors');
  else if (appData !== undefined && appData.trim().length > 0) directory = join(appData, 'CadCopilot', 'connectors');
  else directory = join(homedir(), '.config', 'CadCopilot', 'connectors');
  mkdirSync(directory, { recursive: true });
  return directory;
}

/** A fresh 256-bit token: 64 hex chars from crypto.randomBytes(32). */
export function generateToken(): string {
  return randomBytes(32).toString('hex');
}

/**
 * The pipe a connector of `cad` in process `pid` listens on: `\\.\pipe\cadcopilot-<cad>-<pid>` on
 * Windows, a socket file in os.tmpdir() elsewhere. With CADCOPILOT_HOME set, a short hash of it is
 * appended so parallel test environments never collide.
 */
export function pipeNameFor(cad: CadKind, pid: number): string {
  const home = copilotHomeOverride();
  const suffix = home === undefined ? '' : `-${createHash('sha256').update(home).digest('hex').slice(0, 10)}`;
  const name = `cadcopilot-${cad}-${pid}${suffix}`;
  return process.platform === 'win32' ? `\\\\.\\pipe\\${name}` : join(tmpdir(), `${name}.sock`);
}

function errorCode(error: unknown): string | undefined {
  if (typeof error !== 'object' || error === null) return undefined;
  const code = (error as { code?: unknown }).code;
  return typeof code === 'string' ? code : undefined;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** True when a process with this pid exists (a process we may not signal still counts as alive). */
function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return errorCode(error) === 'EPERM';
  }
}

async function removeFileQuietly(file: string): Promise<void> {
  try {
    await unlink(file);
  } catch {
    // Already gone, or locked by its writer: either way there is nothing more to do here.
  }
}

/**
 * Lists the connectors that are running now, oldest first. Advert files that are malformed or
 * belong to a process that no longer exists are deleted.
 */
export async function listAdverts(): Promise<ConnectorAdvert[]> {
  let directory: string;
  let names: string[];
  try {
    directory = connectorsDirectory();
    names = await readdir(directory);
  } catch {
    // No readable discovery folder means no discoverable connectors.
    return [];
  }

  const adverts: ConnectorAdvert[] = [];
  for (const name of names) {
    if (!name.toLowerCase().endsWith('.json')) continue;
    const file = join(directory, name);
    let text: string;
    try {
      text = await readFile(file, 'utf8');
    } catch {
      // Removed meanwhile, or not readable right now: not an advert we can use, but not ours to delete.
      continue;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      await removeFileQuietly(file);
      continue;
    }
    const advert = connectorAdvertSchema.safeParse(parsed);
    if (!advert.success || !isProcessAlive(advert.data.pid)) {
      await removeFileQuietly(file);
      continue;
    }
    adverts.push(advert.data);
  }

  return adverts.sort((a, b) => {
    const byStart = Date.parse(a.startedAt) - Date.parse(b.startedAt);
    return byStart !== 0 ? byStart : a.id.localeCompare(b.id);
  });
}

// Files to remove when the process exits without closing its connector (advert and, off Windows,
// the socket file). One shared 'exit' listener, however many servers were started.
const filesToRemoveOnExit = new Set<string>();
let exitHookInstalled = false;

function removeOnExit(file: string): void {
  filesToRemoveOnExit.add(file);
  if (exitHookInstalled) return;
  exitHookInstalled = true;
  process.on('exit', () => {
    for (const pending of filesToRemoveOnExit) {
      try {
        unlinkSync(pending);
      } catch {
        // Nothing useful can be done while exiting.
      }
    }
  });
}

// ---------------------------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------------------------

export interface CadConnection {
  readonly advert: ConnectorAdvert;
  hello(): Promise<HelloResult>;
  callTool(params: ToolCallParams, opts?: { signal?: AbortSignal; timeoutMs?: number }): Promise<ToolResult>;
  getWindowBounds(): Promise<WindowBounds | null>;
  setWindowBounds(bounds: WindowBounds): Promise<void>;
  focusWindow(): Promise<void>;
  onNotification(listener: (method: string, params: unknown) => void): () => void;
  onClose(listener: (reason: string) => void): () => void;
  close(): void;
}

/** Options of connectToConnector. */
export interface ConnectOptions {
  /** Client name sent with `auth` (default 'CAD Copilot'). */
  clientName?: string;
  /** Client version sent with `auth` (default '0.0.0'). */
  clientVersion?: string;
  /** Time allowed for connecting and authenticating (default 5 s). */
  connectTimeoutMs?: number;
  /** Time allowed for `hello` and the window methods (default 30 s; 0 disables). */
  requestTimeoutMs?: number;
}

interface PendingRequest {
  resolve(value: unknown): void;
  reject(error: BridgeError): void;
}

function advertLabel(advert: ConnectorAdvert): string {
  return advert.displayName.trim().length > 0 ? advert.displayName : advert.id;
}

class PipeConnection implements CadConnection {
  readonly advert: ConnectorAdvert;
  private readonly socket: Socket;
  private readonly requestTimeoutMs: number;
  private readonly decoder = new NdjsonDecoder();
  private readonly pending = new Map<number | string, PendingRequest>();
  private readonly notificationListeners = new Set<(method: string, params: unknown) => void>();
  private readonly closeListeners = new Set<(reason: string) => void>();
  private nextId = 1;
  private closedReason: string | undefined;
  private closingAnnounced = false;
  private lastSocketError: string | undefined;

  constructor(advert: ConnectorAdvert, socket: Socket, requestTimeoutMs: number) {
    this.advert = advert;
    this.socket = socket;
    this.requestTimeoutMs = requestTimeoutMs;
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string | Buffer) => {
      this.handleData(typeof chunk === 'string' ? chunk : chunk.toString('utf8'));
    });
    socket.on('error', (error) => {
      this.lastSocketError = errorMessage(error);
    });
    socket.on('close', () => {
      if (this.closingAnnounced) this.finalize('connector closing');
      else if (this.lastSocketError !== undefined) this.finalize(`connection error: ${this.lastSocketError}`);
      else this.finalize('connection lost');
    });
  }

  async hello(): Promise<HelloResult> {
    const result = await this.request('hello', {}, { timeoutMs: this.requestTimeoutMs });
    const parsed = helloResultSchema.safeParse(result);
    if (!parsed.success) throw this.malformed('hello', formatValidationIssues(parsed.error.issues));
    return parsed.data;
  }

  async callTool(
    params: ToolCallParams,
    opts: { signal?: AbortSignal; timeoutMs?: number } = {},
  ): Promise<ToolResult> {
    const result = await this.request('tools.call', params, {
      timeoutMs: opts.timeoutMs ?? DEFAULT_TOOL_TIMEOUT_MS,
      ...(opts.signal !== undefined ? { signal: opts.signal } : {}),
    });
    const parsed = toolResultSchema.safeParse(result);
    if (!parsed.success) throw this.malformed('tools.call', formatValidationIssues(parsed.error.issues));
    return parsed.data;
  }

  async getWindowBounds(): Promise<WindowBounds | null> {
    const result = await this.request('window.getBounds', {}, { timeoutMs: this.requestTimeoutMs });
    if (result === null || result === undefined) return null;
    const parsed = windowBoundsSchema.safeParse(result);
    if (!parsed.success) throw this.malformed('window.getBounds', formatValidationIssues(parsed.error.issues));
    return parsed.data;
  }

  async setWindowBounds(bounds: WindowBounds): Promise<void> {
    await this.request('window.setBounds', { bounds }, { timeoutMs: this.requestTimeoutMs });
  }

  async focusWindow(): Promise<void> {
    await this.request('window.focus', {}, { timeoutMs: this.requestTimeoutMs });
  }

  onNotification(listener: (method: string, params: unknown) => void): () => void {
    this.notificationListeners.add(listener);
    return () => {
      this.notificationListeners.delete(listener);
    };
  }

  onClose(listener: (reason: string) => void): () => void {
    const reason = this.closedReason;
    if (reason !== undefined) {
      // Already closed: still tell the late subscriber, once, asynchronously.
      let cancelled = false;
      queueMicrotask(() => {
        if (!cancelled) safeCall(() => listener(reason));
      });
      return () => {
        cancelled = true;
      };
    }
    this.closeListeners.add(listener);
    return () => {
      this.closeListeners.delete(listener);
    };
  }

  close(): void {
    this.shutdown('closed by client');
  }

  /** Closes the connection with a specific reason (used for failed handshakes too). */
  shutdown(reason: string): void {
    this.finalize(reason);
    if (this.socket.destroyed) return;
    this.socket.end();
    const socket = this.socket;
    setTimeout(() => socket.destroy(), CLOSE_GRACE_MS).unref();
  }

  /** Sends a request and resolves with its result; rejects with BridgeError. */
  request(
    method: string,
    params: unknown,
    opts: { signal?: AbortSignal; timeoutMs: number },
  ): Promise<unknown> {
    if (this.closedReason !== undefined) return Promise.reject(this.closedError(this.closedReason));
    const signal = opts.signal;
    if (signal?.aborted === true) {
      return Promise.reject(
        new BridgeError(BridgeErrorCode.Busy, `${method} was cancelled before it was sent`, { reason: 'aborted' }),
      );
    }

    const id = this.nextId++;
    return new Promise<unknown>((resolve, reject) => {
      let timer: ReturnType<typeof setTimeout> | undefined;
      const onAbort = (): void => {
        entry.reject(new BridgeError(BridgeErrorCode.Busy, `${method} was cancelled`, { reason: 'aborted' }));
      };
      const cleanup = (): void => {
        if (timer !== undefined) clearTimeout(timer);
        signal?.removeEventListener('abort', onAbort);
        this.pending.delete(id);
      };
      const entry: PendingRequest = {
        resolve: (value) => {
          cleanup();
          resolve(value);
        },
        reject: (error) => {
          cleanup();
          reject(error);
        },
      };
      this.pending.set(id, entry);

      const timeoutMs = opts.timeoutMs;
      if (Number.isFinite(timeoutMs) && timeoutMs > 0) {
        timer = setTimeout(() => {
          entry.reject(
            new BridgeError(
              BridgeErrorCode.Busy,
              `${advertLabel(this.advert)} did not answer ${method} within ${timeoutMs} ms`,
              { reason: 'timeout' },
            ),
          );
        }, timeoutMs);
      }
      signal?.addEventListener('abort', onAbort, { once: true });

      const message: RpcMessage = { jsonrpc: '2.0', id, method, params };
      this.socket.write(encodeNdjson(message), (error) => {
        if (error) {
          entry.reject(
            new BridgeError(
              BridgeErrorCode.InternalError,
              `Could not send ${method} to ${advertLabel(this.advert)}: ${errorMessage(error)}`,
              { reason: 'connection-closed' },
            ),
          );
        }
      });
    });
  }

  private malformed(method: string, problems: string[]): BridgeError {
    return new BridgeError(
      BridgeErrorCode.InternalError,
      `${advertLabel(this.advert)} sent a malformed ${method} result: ${problems.slice(0, 3).join('; ')}`,
    );
  }

  private closedError(reason: string): BridgeError {
    return new BridgeError(
      BridgeErrorCode.InternalError,
      `The connection to ${advertLabel(this.advert)} is closed (${reason})`,
      { reason: 'connection-closed' },
    );
  }

  private handleData(chunk: string): void {
    if (this.closedReason !== undefined) return;
    // Lines that are not valid JSON cannot be matched to a request; they are dropped.
    const { messages } = this.decoder.push(chunk);
    for (const message of messages) {
      if (isRpcSuccess(message)) {
        this.pending.get(message.id)?.resolve(message.result);
      } else if (isRpcFailure(message)) {
        if (message.id !== null) {
          const { code, message: text, data } = message.error;
          this.pending.get(message.id)?.reject(new BridgeError(code, text, data));
        }
      } else if (isRpcNotification(message)) {
        if (message.method === 'connector.closing') this.closingAnnounced = true;
        for (const listener of [...this.notificationListeners]) {
          safeCall(() => listener(message.method, message.params));
        }
      } else if (isRpcRequest(message)) {
        // Connectors never call the client; answer so a confused peer does not wait forever.
        this.socket.write(
          encodeNdjson({
            jsonrpc: '2.0',
            id: message.id,
            error: { code: BridgeErrorCode.MethodNotFound, message: `Unknown method "${message.method}"` },
          }),
        );
      }
    }
  }

  private finalize(reason: string): void {
    if (this.closedReason !== undefined) return;
    this.closedReason = reason;
    const error = this.closedError(reason);
    for (const entry of [...this.pending.values()]) entry.reject(error);
    this.pending.clear();
    const listeners = [...this.closeListeners];
    this.closeListeners.clear();
    this.notificationListeners.clear();
    for (const listener of listeners) safeCall(() => listener(reason));
  }
}

function safeCall(fn: () => void): void {
  try {
    fn();
  } catch {
    // A listener that throws must not break the transport or the other listeners.
  }
}

/**
 * Connects over the named pipe and authenticates with the advert token. Rejects with a
 * BridgeError when the pipe cannot be opened (InternalError), when connecting and authenticating
 * take longer than 5 s (Busy) or when the connector refuses the token (Unauthorized).
 */
export async function connectToConnector(advert: ConnectorAdvert, opts: ConnectOptions = {}): Promise<CadConnection> {
  const label = advertLabel(advert);
  const connectTimeoutMs =
    opts.connectTimeoutMs !== undefined && Number.isFinite(opts.connectTimeoutMs) && opts.connectTimeoutMs > 0
      ? opts.connectTimeoutMs
      : DEFAULT_CONNECT_TIMEOUT_MS;
  const requestTimeoutMs = opts.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;

  return new Promise<CadConnection>((resolve, reject) => {
    const socket = connect(advert.pipeName);
    const connection = new PipeConnection(advert, socket, requestTimeoutMs);
    let settled = false;

    const fail = (error: BridgeError): void => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      connection.shutdown('connection attempt failed');
      socket.destroy();
      reject(error);
    };

    const timer = setTimeout(() => {
      fail(
        new BridgeError(BridgeErrorCode.Busy, `Timed out connecting to ${label} after ${connectTimeoutMs} ms`, {
          reason: 'timeout',
        }),
      );
    }, connectTimeoutMs);

    socket.once('error', (error) => {
      fail(
        new BridgeError(
          BridgeErrorCode.InternalError,
          `Could not connect to ${label} (${advert.pipeName}): ${errorMessage(error)}`,
          { reason: 'connect-failed' },
        ),
      );
    });

    socket.once('connect', () => {
      const params = {
        token: advert.token,
        client: { name: opts.clientName ?? 'CAD Copilot', version: opts.clientVersion ?? '0.0.0' },
      };
      connection.request('auth', params, { timeoutMs: 0 }).then(
        () => {
          if (settled) return;
          settled = true;
          clearTimeout(timer);
          resolve(connection);
        },
        (error: unknown) => {
          if (isBridgeError(error) && error.code === BridgeErrorCode.Unauthorized) {
            fail(new BridgeError(error.code, `${label} refused the connection: ${error.message}`, error.data));
          } else if (isBridgeError(error)) {
            fail(new BridgeError(error.code, `Could not authenticate with ${label}: ${error.message}`, error.data));
          } else {
            fail(
              new BridgeError(
                BridgeErrorCode.InternalError,
                `Could not authenticate with ${label}: ${errorMessage(error)}`,
              ),
            );
          }
        },
      );
    });
  });
}

// ---------------------------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------------------------

/** Handlers a connector implements (CAD Studio main process uses this; AutoCAD's C# plugin mirrors it). */
export interface ConnectorHandlers {
  hello(): Promise<HelloResult>;
  callTool(params: ToolCallParams): Promise<ToolResult>;
  getWindowBounds(): Promise<WindowBounds | null>;
  setWindowBounds(bounds: WindowBounds): Promise<void>;
  focusWindow(): Promise<void>;
}

export interface ConnectorServer {
  readonly advert: ConnectorAdvert;
  /** Number of authenticated clients currently connected. */
  readonly clientCount: number;
  /** Broadcasts a notification to every authenticated client. */
  notify(method: string, params: unknown): void;
  /** Sends `connector.closing`, closes every connection and the pipe, deletes the advert file. */
  close(): Promise<void>;
}

interface ClientState {
  readonly socket: Socket;
  readonly decoder: NdjsonDecoder;
  authenticated: boolean;
  /** Set once the connection is being dropped: further input is ignored. */
  stopped: boolean;
  /** Tail of the per-connection work queue (requests run one at a time, in arrival order). */
  queue: Promise<void>;
  authTimer: ReturnType<typeof setTimeout> | undefined;
  /** Resolves once the socket has closed and the client was removed from the bookkeeping. */
  readonly closed: Promise<void>;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Methods without params accept a missing `params` or any object, nothing else. */
function requireNoParams(method: string, params: unknown): void {
  if (params === undefined || params === null || isPlainObject(params)) return;
  throw new BridgeError(BridgeErrorCode.InvalidParams, `Invalid params for ${method}: expected an object`);
}

function invalidParams(method: string, issues: Parameters<typeof formatValidationIssues>[0]): BridgeError {
  return new BridgeError(
    BridgeErrorCode.InvalidParams,
    `Invalid params for ${method}: ${formatValidationIssues(issues).slice(0, 5).join('; ')}`,
  );
}

/**
 * Starts a named-pipe server, writes the advert file, enforces auth, dispatches to handlers.
 * Handlers may throw BridgeError to answer with a specific JSON-RPC error code; any other thrown
 * error becomes InternalError (-32603). Requests of one connection run one at a time, in arrival
 * order. The advert is removed by `close()` and, as a fallback, when the process exits.
 */
export async function startConnectorServer(opts: {
  cad: CadKind;
  displayName: string;
  cadVersion: string;
  connectorVersion: string;
  handlers: ConnectorHandlers;
  /** Called whenever the number of authenticated clients changes. */
  onClientsChanged?: (count: number) => void;
  /** Time a new connection gets to authenticate before it is dropped (default 10 s). */
  authTimeoutMs?: number;
}): Promise<ConnectorServer> {
  const { handlers } = opts;
  const pid = process.pid;
  const token = generateToken();
  const expectedTokenDigest = createHash('sha256').update(token).digest();
  const pipeName = pipeNameFor(opts.cad, pid);
  const advert: ConnectorAdvert = {
    id: `${opts.cad}-${pid}`,
    cad: opts.cad,
    displayName: opts.displayName,
    cadVersion: opts.cadVersion,
    connectorVersion: opts.connectorVersion,
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    pipeName,
    token,
    pid,
    startedAt: new Date().toISOString(),
  };
  const directory = connectorsDirectory();
  const advertFile = join(directory, `${advert.id}.json`);

  const authTimeoutMs =
    opts.authTimeoutMs !== undefined && Number.isFinite(opts.authTimeoutMs) && opts.authTimeoutMs > 0
      ? opts.authTimeoutMs
      : AUTH_DEADLINE_MS;

  const clients = new Set<ClientState>();
  let authenticatedCount = 0;
  let closing: Promise<void> | undefined;

  const clientsChanged = (): void => {
    const listener = opts.onClientsChanged;
    if (listener !== undefined) safeCall(() => listener(authenticatedCount));
  };

  const tokenMatches = (candidate: string): boolean =>
    timingSafeEqual(createHash('sha256').update(candidate).digest(), expectedTokenDigest);

  const send = (client: ClientState, message: RpcMessage): void => {
    if (client.stopped || client.socket.destroyed || !client.socket.writable) return;
    client.socket.write(encodeNdjson(message));
  };

  /** Sends a final error and closes the connection; anything the client sent after it is ignored. */
  const refuse = (client: ClientState, id: number | string | null, code: number, message: string): void => {
    if (client.stopped) return;
    client.stopped = true;
    const { socket } = client;
    if (socket.destroyed) return;
    if (socket.writable) socket.end(encodeNdjson({ jsonrpc: '2.0', id, error: { code, message } }));
    setTimeout(() => socket.destroy(), CLOSE_GRACE_MS).unref();
  };

  const dispatch = async (method: string, params: unknown): Promise<unknown> => {
    switch (method) {
      case 'hello':
        requireNoParams(method, params);
        return handlers.hello();
      case 'tools.call': {
        const parsed = toolCallParamsSchema.safeParse(params);
        if (!parsed.success) throw invalidParams(method, parsed.error.issues);
        const result = await handlers.callTool(parsed.data);
        const checked = toolResultSchema.safeParse(result);
        if (!checked.success) {
          throw new BridgeError(
            BridgeErrorCode.InternalError,
            `The connector produced a malformed tool result: ${formatValidationIssues(checked.error.issues)
              .slice(0, 3)
              .join('; ')}`,
          );
        }
        return result;
      }
      case 'window.getBounds':
        requireNoParams(method, params);
        return (await handlers.getWindowBounds()) ?? null;
      case 'window.setBounds': {
        const parsed = setWindowBoundsParamsSchema.safeParse(params);
        if (!parsed.success) throw invalidParams(method, parsed.error.issues);
        await handlers.setWindowBounds(parsed.data.bounds);
        return { ok: true };
      }
      case 'window.focus':
        requireNoParams(method, params);
        await handlers.focusWindow();
        return { ok: true };
      default:
        throw new BridgeError(BridgeErrorCode.MethodNotFound, `Unknown method "${method}"`);
    }
  };

  const handleMessage = async (client: ClientState, message: unknown): Promise<void> => {
    if (client.stopped) return;

    if (!isRpcRequest(message)) {
      if (!client.authenticated) {
        refuse(client, null, BridgeErrorCode.Unauthorized, 'Unauthorized: the first request must be "auth"');
        return;
      }
      // Notifications and stray responses need no answer; anything else is not JSON-RPC.
      if (isRpcNotification(message) || isRpcSuccess(message) || isRpcFailure(message)) return;
      send(client, {
        jsonrpc: '2.0',
        id: null,
        error: { code: BridgeErrorCode.InvalidRequest, message: 'Invalid request: expected a JSON-RPC 2.0 request' },
      });
      return;
    }

    const { id, method, params } = message;

    if (method === 'auth') {
      const parsed = authParamsSchema.safeParse(params);
      if (!parsed.success || !tokenMatches(parsed.data.token)) {
        refuse(client, id, BridgeErrorCode.Unauthorized, 'Unauthorized: invalid token');
        return;
      }
      if (!client.authenticated) {
        client.authenticated = true;
        if (client.authTimer !== undefined) clearTimeout(client.authTimer);
        client.authTimer = undefined;
        authenticatedCount += 1;
        send(client, { jsonrpc: '2.0', id, result: { ok: true } });
        clientsChanged();
        return;
      }
      send(client, { jsonrpc: '2.0', id, result: { ok: true } });
      return;
    }

    if (!client.authenticated) {
      refuse(client, id, BridgeErrorCode.Unauthorized, 'Unauthorized: the first request must be "auth"');
      return;
    }

    try {
      const result = await dispatch(method, params);
      send(client, { jsonrpc: '2.0', id, result: result === undefined ? null : result });
    } catch (error) {
      if (isBridgeError(error)) {
        const failure: RpcMessage = {
          jsonrpc: '2.0',
          id,
          error:
            error.data === undefined
              ? { code: error.code, message: error.message }
              : { code: error.code, message: error.message, data: error.data },
        };
        send(client, failure);
      } else {
        send(client, {
          jsonrpc: '2.0',
          id,
          error: { code: BridgeErrorCode.InternalError, message: errorMessage(error) || 'Internal error' },
        });
      }
    }
  };

  const onConnection = (socket: Socket): void => {
    if (closing !== undefined) {
      socket.on('error', () => undefined);
      socket.destroy();
      return;
    }
    let markClosed: () => void = () => undefined;
    const client: ClientState = {
      socket,
      decoder: new NdjsonDecoder(),
      authenticated: false,
      stopped: false,
      queue: Promise.resolve(),
      authTimer: undefined,
      closed: new Promise<void>((resolve) => {
        markClosed = resolve;
      }),
    };
    clients.add(client);
    client.authTimer = setTimeout(() => {
      if (!client.authenticated) {
        refuse(client, null, BridgeErrorCode.Unauthorized, 'Unauthorized: no "auth" request was received in time');
      }
    }, authTimeoutMs);
    client.authTimer.unref();

    socket.setEncoding('utf8');
    socket.on('data', (chunk: string | Buffer) => {
      if (client.stopped) return;
      const { messages, errors } = client.decoder.push(typeof chunk === 'string' ? chunk : chunk.toString('utf8'));
      for (const problem of errors) {
        if (!client.authenticated) {
          refuse(client, null, BridgeErrorCode.ParseError, `Parse error: ${problem}`);
          return;
        }
        send(client, {
          jsonrpc: '2.0',
          id: null,
          error: { code: BridgeErrorCode.ParseError, message: `Parse error: ${problem}` },
        });
      }
      for (const message of messages) {
        client.queue = client.queue.then(() => handleMessage(client, message)).catch(() => undefined);
      }
    });
    socket.on('error', () => {
      // The 'close' event that follows does the cleanup.
    });
    socket.on('close', () => {
      client.stopped = true;
      if (client.authTimer !== undefined) clearTimeout(client.authTimer);
      client.authTimer = undefined;
      clients.delete(client);
      if (client.authenticated) {
        client.authenticated = false;
        authenticatedCount -= 1;
        clientsChanged();
      }
      markClosed();
    });
  };

  const server: Server = createServer(onConnection);
  if (process.platform !== 'win32') {
    // A socket file left behind by a crashed process with the same pid would block listen().
    await removeFileQuietly(pipeName);
  }
  try {
    await new Promise<void>((resolve, reject) => {
      const onError = (error: Error): void => reject(error);
      server.once('error', onError);
      server.listen(pipeName, () => {
        server.off('error', onError);
        resolve();
      });
    });
  } catch (error) {
    throw new Error(`Could not start the ${opts.displayName} connector on ${pipeName}: ${errorMessage(error)}`, {
      cause: error,
    });
  }
  server.on('error', () => {
    // Accept errors after start-up are not fatal for the connections that already exist.
  });

  const closeServer = (): Promise<void> =>
    new Promise<void>((resolve) => {
      server.close(() => resolve());
    });

  try {
    // Atomic: readers never see a half-written advert.
    const temporary = `${advertFile}.${randomBytes(4).toString('hex')}.tmp`;
    await writeFile(temporary, `${JSON.stringify(advert, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 });
    try {
      await rename(temporary, advertFile);
    } catch (error) {
      await removeFileQuietly(temporary);
      throw error;
    }
  } catch (error) {
    await closeServer();
    throw new Error(`Could not write the connector advert ${advertFile}: ${errorMessage(error)}`, { cause: error });
  }
  removeOnExit(advertFile);
  if (process.platform !== 'win32') removeOnExit(pipeName);

  const notify = (method: string, params: unknown): void => {
    if (closing !== undefined) return;
    const line = encodeNdjson(params === undefined ? { jsonrpc: '2.0', method } : { jsonrpc: '2.0', method, params });
    for (const client of clients) {
      if (client.authenticated && !client.stopped && !client.socket.destroyed && client.socket.writable) {
        client.socket.write(line);
      }
    }
  };

  const close = (): Promise<void> => {
    if (closing !== undefined) return closing;
    notify('connector.closing', {});
    closing = (async () => {
      const closed = closeServer();
      const open = [...clients];
      for (const client of open) {
        client.stopped = true;
        if (!client.socket.destroyed) client.socket.end();
      }
      const force = setTimeout(() => {
        for (const client of open) client.socket.destroy();
      }, CLOSE_GRACE_MS);
      await removeFileQuietly(advertFile);
      filesToRemoveOnExit.delete(advertFile);
      // Wait for the sockets themselves, not only the listener: clientCount and onClientsChanged
      // are settled when close() resolves.
      await Promise.all(open.map((client) => client.closed));
      clearTimeout(force);
      await closed;
      if (process.platform !== 'win32') filesToRemoveOnExit.delete(pipeName);
    })();
    return closing;
  };

  return {
    advert,
    get clientCount() {
      return authenticatedCount;
    },
    notify,
    close,
  };
}
