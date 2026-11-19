import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { connect, createServer } from 'node:net';
import type { Server, Socket } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, afterEach, describe, expect, it } from 'vitest';
import { BRIDGE_PROTOCOL_VERSION, BridgeError, BridgeErrorCode, NdjsonDecoder } from './index';
import type { ConnectorAdvert, HelloResult, ToolCallParams, ToolResult, WindowBounds } from './index';
import {
  connectToConnector,
  connectorsDirectory,
  generateToken,
  listAdverts,
  pipeNameFor,
  startConnectorServer,
} from './node';
import type { CadConnection, ConnectorHandlers, ConnectorServer } from './node';

// Every test file gets its own discovery folder (and, through the hash suffix, its own pipe names).
const HOME = mkdtempSync(join(tmpdir(), 'cadcopilot-transport-'));
process.env.CADCOPILOT_HOME = HOME;

const servers: ConnectorServer[] = [];
const connections: CadConnection[] = [];
const rawSockets: Socket[] = [];
const rawServers: Server[] = [];

afterEach(async () => {
  for (const connection of connections.splice(0)) connection.close();
  for (const socket of rawSockets.splice(0)) socket.destroy();
  await Promise.all(servers.splice(0).map((server) => server.close()));
  await Promise.all(
    rawServers.splice(0).map((server) => new Promise<void>((resolve) => server.close(() => resolve()))),
  );
});

afterAll(() => {
  rmSync(HOME, { recursive: true, force: true });
});

const HELLO: HelloResult = {
  cad: 'cadstudio',
  cadVersion: '1.2.3',
  connectorVersion: '0.1.0',
  protocolVersion: BRIDGE_PROTOCOL_VERSION,
  document: { id: 'doc-1', name: 'Plan.cadstudio', path: null, unit: 'ft', revision: 7, entityCount: 3 },
  supportedTools: ['get_drawing_info', 'create_line'],
};

function okResult(data: unknown, revision = 1): ToolResult {
  return { success: true, data, createdIds: [], modifiedIds: [], deletedIds: [], warnings: [], errors: [], revision };
}

function makeHandlers(overrides: Partial<ConnectorHandlers> = {}): ConnectorHandlers {
  return {
    hello: async () => HELLO,
    callTool: async (params) => okResult({ echo: params.arguments }),
    getWindowBounds: async () => null,
    setWindowBounds: async () => undefined,
    focusWindow: async () => undefined,
    ...overrides,
  };
}

async function startServer(
  overrides: Partial<ConnectorHandlers> = {},
  extra: { cad?: 'cadstudio' | 'autocad'; onClientsChanged?: (count: number) => void } = {},
): Promise<ConnectorServer> {
  const server = await startConnectorServer({
    cad: extra.cad ?? 'cadstudio',
    displayName: 'CAD Studio (test)',
    cadVersion: '1.2.3',
    connectorVersion: '0.1.0',
    handlers: makeHandlers(overrides),
    ...(extra.onClientsChanged !== undefined ? { onClientsChanged: extra.onClientsChanged } : {}),
  });
  servers.push(server);
  return server;
}

async function connectClient(advert: ConnectorAdvert): Promise<CadConnection> {
  const connection = await connectToConnector(advert, { clientName: 'vitest', clientVersion: '9.9.9' });
  connections.push(connection);
  return connection;
}

function callParams(name: string, args: unknown, extra: Partial<ToolCallParams> = {}): ToolCallParams {
  return { requestId: `req-${name}`, operationId: `op-${name}`, name, arguments: args, source: 'ai', ...extra };
}

async function waitFor(condition: () => boolean, timeoutMs = 3000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) throw new Error('waitFor timed out');
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

function deferred<T = void>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

/** Captures the rejection of a promise as a value. */
async function rejection(promise: Promise<unknown>): Promise<unknown> {
  try {
    await promise;
  } catch (error) {
    return error;
  }
  throw new Error('expected the promise to reject');
}

function expectBridgeError(error: unknown, code: number, message?: RegExp): BridgeError {
  expect(error).toBeInstanceOf(BridgeError);
  const bridgeError = error as BridgeError;
  expect(bridgeError.code).toBe(code);
  if (message !== undefined) expect(bridgeError.message).toMatch(message);
  return bridgeError;
}

/** A bare NDJSON client for protocol-level tests. */
async function rawClient(pipeName: string): Promise<{
  socket: Socket;
  send(value: unknown): void;
  next(): Promise<unknown>;
  received: unknown[];
  closed: Promise<void>;
}> {
  const socket = connect(pipeName);
  rawSockets.push(socket);
  socket.setEncoding('utf8');
  const decoder = new NdjsonDecoder();
  const received: unknown[] = [];
  let consumed = 0;
  socket.on('data', (chunk: string | Buffer) => {
    received.push(...decoder.push(String(chunk)).messages);
  });
  socket.on('error', () => undefined);
  const closed = new Promise<void>((resolve) => socket.on('close', () => resolve()));
  await new Promise<void>((resolve, reject) => {
    socket.once('connect', () => resolve());
    socket.once('error', reject);
  });
  return {
    socket,
    received,
    closed,
    send(value) {
      socket.write(typeof value === 'string' ? value : `${JSON.stringify(value)}\n`);
    },
    async next() {
      await waitFor(() => received.length > consumed);
      const message = received[consumed];
      consumed += 1;
      return message;
    },
  };
}

async function authedRawClient(advert: ConnectorAdvert): ReturnType<typeof rawClient> {
  const client = await rawClient(advert.pipeName);
  client.send({ jsonrpc: '2.0', id: 'auth', method: 'auth', params: { token: advert.token } });
  expect(await client.next()).toEqual({ jsonrpc: '2.0', id: 'auth', result: { ok: true } });
  return client;
}

function advertFiles(): string[] {
  return readdirSync(connectorsDirectory()).sort();
}

describe('discovery helpers', () => {
  it('uses CADCOPILOT_HOME/connectors and creates it', () => {
    const directory = connectorsDirectory();
    expect(directory).toBe(join(HOME, 'connectors'));
    expect(existsSync(directory)).toBe(true);
  });

  it('falls back to %APPDATA%/CadCopilot/connectors', () => {
    const appData = mkdtempSync(join(tmpdir(), 'cadcopilot-appdata-'));
    const previousAppData = process.env.APPDATA;
    delete process.env.CADCOPILOT_HOME;
    process.env.APPDATA = appData;
    try {
      const directory = connectorsDirectory();
      expect(directory).toBe(join(appData, 'CadCopilot', 'connectors'));
      expect(existsSync(directory)).toBe(true);
    } finally {
      process.env.CADCOPILOT_HOME = HOME;
      if (previousAppData === undefined) delete process.env.APPDATA;
      else process.env.APPDATA = previousAppData;
      rmSync(appData, { recursive: true, force: true });
    }
  });

  it('generates 256-bit hex tokens that never repeat', () => {
    const tokens = new Set(Array.from({ length: 50 }, () => generateToken()));
    expect(tokens.size).toBe(50);
    for (const token of tokens) expect(token).toMatch(/^[0-9a-f]{64}$/);
  });

  it('derives the pipe name from the CAD kind and pid', () => {
    const name = pipeNameFor('cadstudio', 1234);
    if (process.platform === 'win32') {
      expect(name).toMatch(/^\\\\\.\\pipe\\cadcopilot-cadstudio-1234-[0-9a-f]{10}$/);
    } else {
      expect(name).toMatch(/cadcopilot-cadstudio-1234-[0-9a-f]{10}\.sock$/);
    }
    expect(pipeNameFor('cadstudio', 1234)).toBe(name);
    expect(pipeNameFor('autocad', 1234)).not.toBe(name);
    expect(pipeNameFor('cadstudio', 1235)).not.toBe(name);
  });

  it('isolates pipe names per CADCOPILOT_HOME and uses the plain name without it', () => {
    const inThisHome = pipeNameFor('autocad', 42);
    try {
      process.env.CADCOPILOT_HOME = `${HOME}-other`;
      expect(pipeNameFor('autocad', 42)).not.toBe(inThisHome);
      delete process.env.CADCOPILOT_HOME;
      const plain = pipeNameFor('autocad', 42);
      if (process.platform === 'win32') expect(plain).toBe('\\\\.\\pipe\\cadcopilot-autocad-42');
      else expect(plain).toBe(join(tmpdir(), 'cadcopilot-autocad-42.sock'));
    } finally {
      process.env.CADCOPILOT_HOME = HOME;
    }
  });
});

describe('advert lifecycle', () => {
  it('writes the advert on start and removes it on close', async () => {
    const server = await startServer();
    const { advert } = server;
    expect(advert).toMatchObject({
      id: `cadstudio-${process.pid}`,
      cad: 'cadstudio',
      displayName: 'CAD Studio (test)',
      cadVersion: '1.2.3',
      connectorVersion: '0.1.0',
      protocolVersion: BRIDGE_PROTOCOL_VERSION,
      pipeName: pipeNameFor('cadstudio', process.pid),
      pid: process.pid,
    });
    expect(advert.token).toMatch(/^[0-9a-f]{64}$/);
    expect(Number.isNaN(Date.parse(advert.startedAt))).toBe(false);

    const file = join(connectorsDirectory(), `cadstudio-${process.pid}.json`);
    expect(JSON.parse(readFileSync(file, 'utf8'))).toEqual(advert);
    expect(advertFiles()).toEqual([`cadstudio-${process.pid}.json`]);
    expect(await listAdverts()).toEqual([advert]);

    await server.close();
    expect(existsSync(file)).toBe(false);
    expect(advertFiles()).toEqual([]);
    expect(await listAdverts()).toEqual([]);
  });

  it('uses a fresh token for every server', async () => {
    const first = await startServer();
    const firstToken = first.advert.token;
    await first.close();
    const second = await startServer();
    expect(second.advert.token).not.toBe(firstToken);
  });

  it('refuses to start two connectors of the same CAD in one process', async () => {
    await startServer();
    await expect(
      startConnectorServer({
        cad: 'cadstudio',
        displayName: 'Second',
        cadVersion: '1',
        connectorVersion: '1',
        handlers: makeHandlers(),
      }),
    ).rejects.toThrow(/Could not start the Second connector/);
    // The first connector's advert is untouched.
    expect(await listAdverts()).toHaveLength(1);
  });

  it('drops and deletes stale and malformed adverts, keeps live ones sorted by start time', async () => {
    const directory = connectorsDirectory();
    const deadPid = spawnSync(process.execPath, ['-e', '']).pid;
    expect(deadPid).toBeGreaterThan(0);

    const live = (id: string, startedAt: string): ConnectorAdvert => ({
      id,
      cad: 'autocad',
      displayName: 'AutoCAD 2026',
      cadVersion: '25.1',
      connectorVersion: '0.1.0',
      protocolVersion: 1,
      pipeName: `\\\\.\\pipe\\${id}`,
      token: 'ab'.repeat(32),
      pid: process.pid,
      startedAt,
    });
    const newer = live('autocad-newer', '2026-03-01T10:00:00.000Z');
    const older = live('autocad-older', '2026-01-01T10:00:00.000Z');
    const middle = live('autocad-middle', '2026-02-01T10:00:00.000Z');
    writeFileSync(join(directory, 'autocad-newer.json'), JSON.stringify(newer));
    writeFileSync(join(directory, 'autocad-older.json'), JSON.stringify(older));
    writeFileSync(join(directory, 'autocad-middle.json'), JSON.stringify(middle));

    writeFileSync(join(directory, 'stale.json'), JSON.stringify({ ...newer, id: 'stale', pid: deadPid }));
    writeFileSync(join(directory, 'broken.json'), '{ "id": "broken", ');
    writeFileSync(join(directory, 'empty.json'), '');
    writeFileSync(join(directory, 'array.json'), '[1, 2, 3]');
    writeFileSync(join(directory, 'short-token.json'), JSON.stringify({ ...newer, token: 'abc' }));
    writeFileSync(join(directory, 'bad-cad.json'), JSON.stringify({ ...newer, cad: 'sketchup' }));
    writeFileSync(join(directory, 'bad-pid.json'), JSON.stringify({ ...newer, pid: -4 }));
    writeFileSync(join(directory, 'bad-date.json'), JSON.stringify({ ...newer, startedAt: 'yesterday' }));
    const { pipeName: _omitted, ...withoutPipe } = newer;
    writeFileSync(join(directory, 'no-pipe.json'), JSON.stringify(withoutPipe));
    writeFileSync(join(directory, 'notes.txt'), 'not an advert');
    writeFileSync(join(directory, 'half-written.json.1a2b3c4d.tmp'), '{');

    const adverts = await listAdverts();
    expect(adverts).toEqual([older, middle, newer]);
    expect(advertFiles()).toEqual([
      'autocad-middle.json',
      'autocad-newer.json',
      'autocad-older.json',
      'half-written.json.1a2b3c4d.tmp',
      'notes.txt',
    ]);

    // A second pass is stable.
    expect(await listAdverts()).toEqual([older, middle, newer]);
    for (const name of advertFiles()) rmSync(join(directory, name));
  });

  it('lists a running server next to other adverts', async () => {
    const studio = await startServer();
    const autocad = await startServer({}, { cad: 'autocad' });
    const adverts = await listAdverts();
    expect(adverts.map((advert) => advert.id).sort()).toEqual([`autocad-${process.pid}`, `cadstudio-${process.pid}`]);
    expect(adverts.map((advert) => advert.startedAt)).toEqual(
      [studio.advert.startedAt, autocad.advert.startedAt].sort(),
    );
    expect(studio.advert.pipeName).not.toBe(autocad.advert.pipeName);
  });
});

describe('authentication', () => {
  it('connects with the advert token', async () => {
    const server = await startServer();
    const connection = await connectClient(server.advert);
    expect(connection.advert).toBe(server.advert);
    expect(server.clientCount).toBe(1);
  });

  it('rejects a wrong token with Unauthorized', async () => {
    const calls: string[] = [];
    const server = await startServer({
      hello: async () => {
        calls.push('hello');
        return HELLO;
      },
    });
    const error = await rejection(connectToConnector({ ...server.advert, token: generateToken() }));
    expectBridgeError(error, BridgeErrorCode.Unauthorized, /CAD Studio \(test\) refused the connection.*invalid token/);
    const shortToken = await rejection(connectToConnector({ ...server.advert, token: 'short' }));
    expectBridgeError(shortToken, BridgeErrorCode.Unauthorized);
    expect(server.clientCount).toBe(0);
    expect(calls).toEqual([]);
  });

  it('answers a wrong token with -32001 and closes the socket', async () => {
    const server = await startServer();
    const client = await rawClient(server.advert.pipeName);
    client.send({ jsonrpc: '2.0', id: 1, method: 'auth', params: { token: 'f'.repeat(64) } });
    expect(await client.next()).toEqual({
      jsonrpc: '2.0',
      id: 1,
      error: { code: -32001, message: 'Unauthorized: invalid token' },
    });
    await client.closed;
    expect(server.clientCount).toBe(0);
  });

  it('rejects auth params of the wrong shape', async () => {
    const server = await startServer();
    for (const params of [undefined, {}, { token: 42 }, [server.advert.token], server.advert.token]) {
      const client = await rawClient(server.advert.pipeName);
      client.send({ jsonrpc: '2.0', id: 1, method: 'auth', ...(params === undefined ? {} : { params }) });
      expect(await client.next()).toMatchObject({ id: 1, error: { code: -32001 } });
      await client.closed;
    }
    expect(server.clientCount).toBe(0);
  });

  it('rejects any request before auth with -32001 and closes the socket', async () => {
    let helloCalls = 0;
    const server = await startServer({
      hello: async () => {
        helloCalls += 1;
        return HELLO;
      },
    });
    const client = await rawClient(server.advert.pipeName);
    client.send({ jsonrpc: '2.0', id: 5, method: 'hello', params: {} });
    // Sent in the same breath: must be ignored once the connection was refused.
    client.send({ jsonrpc: '2.0', id: 6, method: 'auth', params: { token: server.advert.token } });
    expect(await client.next()).toEqual({
      jsonrpc: '2.0',
      id: 5,
      error: { code: -32001, message: 'Unauthorized: the first request must be "auth"' },
    });
    await client.closed;
    expect(client.received).toHaveLength(1);
    expect(helloCalls).toBe(0);
    expect(server.clientCount).toBe(0);
  });

  it('rejects notifications and garbage before auth', async () => {
    const server = await startServer();
    const notifying = await rawClient(server.advert.pipeName);
    notifying.send({ jsonrpc: '2.0', method: 'hello' });
    expect(await notifying.next()).toMatchObject({ id: null, error: { code: -32001 } });
    await notifying.closed;

    const garbage = await rawClient(server.advert.pipeName);
    garbage.send('this is not json\n');
    expect(await garbage.next()).toMatchObject({ id: null, error: { code: -32700 } });
    await garbage.closed;
    expect(server.clientCount).toBe(0);
  });

  it('drops a connection that does not authenticate in time', async () => {
    const server = await startConnectorServer({
      cad: 'cadstudio',
      displayName: 'Impatient',
      cadVersion: '1',
      connectorVersion: '1',
      handlers: makeHandlers(),
      authTimeoutMs: 80,
    });
    servers.push(server);
    const idle = await rawClient(server.advert.pipeName);
    const prompt = await authedRawClient(server.advert);
    expect(await idle.next()).toEqual({
      jsonrpc: '2.0',
      id: null,
      error: { code: -32001, message: 'Unauthorized: no "auth" request was received in time' },
    });
    await idle.closed;
    // The authenticated connection is not affected by the deadline.
    await new Promise((resolve) => setTimeout(resolve, 120));
    prompt.send({ jsonrpc: '2.0', id: 9, method: 'hello' });
    expect(await prompt.next()).toMatchObject({ id: 9, result: { cad: 'cadstudio' } });
    expect(server.clientCount).toBe(1);
  });

  it('accepts a repeated auth on an authenticated connection without counting it twice', async () => {
    const counts: number[] = [];
    const server = await startServer({}, { onClientsChanged: (count) => counts.push(count) });
    const client = await authedRawClient(server.advert);
    client.send({ jsonrpc: '2.0', id: 2, method: 'auth', params: { token: server.advert.token } });
    expect(await client.next()).toEqual({ jsonrpc: '2.0', id: 2, result: { ok: true } });
    expect(server.clientCount).toBe(1);
    expect(counts).toEqual([1]);
  });
});

describe('requests', () => {
  it('answers hello', async () => {
    const server = await startServer();
    const connection = await connectClient(server.advert);
    expect(await connection.hello()).toEqual(HELLO);
    expect(await connection.hello()).toEqual(HELLO);
  });

  it('answers sequential requests without added latency', async () => {
    const server = await startServer();
    const connection = await connectClient(server.advert);
    const startedAt = performance.now();
    for (let index = 0; index < 200; index += 1) {
      const result = await connection.callTool(callParams('get_drawing_revision', { index }));
      expect(result.data).toEqual({ echo: { index } });
    }
    const elapsed = performance.now() - startedAt;
    // Generous bound for a busy machine (25 ms per round trip; typically under 3 ms). A Nagle-style
    // 40 ms delay per message would still blow past it.
    expect(elapsed).toBeLessThan(5000);
  }, 15_000);

  it('round-trips a tool call with its params intact', async () => {
    const seen: ToolCallParams[] = [];
    const result: ToolResult = {
      success: true,
      data: { entities: [{ id: 'e9', type: 'rectangle' }] },
      createdIds: ['e9'],
      modifiedIds: [],
      deletedIds: [],
      warnings: ['Layer "Walls" is locked'],
      errors: [],
      revision: 12,
    };
    const server = await startServer({
      callTool: async (params) => {
        seen.push(params);
        return result;
      },
    });
    const connection = await connectClient(server.advert);
    const params: ToolCallParams = {
      requestId: 'req-1',
      operationId: 'op-1',
      name: 'create_rectangle',
      arguments: { corner: { x: 0, y: 0 }, width: 12, height: 14, unit: 'ft' },
      expectedRevision: 11,
      source: 'ai',
      instruction: 'Draw a 12 x 14 ft bedroom\nwith "quotes" and unicode ✓',
    };
    expect(await connection.callTool(params)).toEqual(result);
    expect(seen).toEqual([params]);
  });

  it('passes a failed ToolResult through as a result, not as an error', async () => {
    const failed: ToolResult = {
      success: false,
      createdIds: [],
      modifiedIds: [],
      deletedIds: [],
      warnings: [],
      errors: ['The drawing changed since revision 3; inspect it again'],
      revision: 5,
    };
    const server = await startServer({ callTool: async () => failed });
    const connection = await connectClient(server.advert);
    expect(await connection.callTool(callParams('move_entities', { ids: ['a'], dx: 1, dy: 1 }))).toEqual(failed);
  });

  it('carries a 1 MiB payload in both directions', async () => {
    const server = await startServer();
    const connection = await connectClient(server.advert);
    const big = 'ab✓'.repeat(350_000) + 'line\nbreak';
    expect(Buffer.byteLength(big, 'utf8')).toBeGreaterThan(1024 * 1024);
    const result = await connection.callTool(callParams('create_text', { text: big, nested: [big.slice(0, 10)] }));
    expect(result.success).toBe(true);
    const data = result.data as { echo: { text: string; nested: string[] } };
    expect(data.echo.text.length).toBe(big.length);
    expect(data.echo.text === big).toBe(true);
    expect(data.echo.nested).toEqual([big.slice(0, 10)]);
    // The connection is still healthy afterwards.
    expect(await connection.hello()).toEqual(HELLO);
  });

  it('answers 20 concurrent calls in order, one at a time', async () => {
    const started: number[] = [];
    let running = 0;
    let maxRunning = 0;
    const server = await startServer({
      callTool: async (params) => {
        const index = (params.arguments as { index: number }).index;
        started.push(index);
        running += 1;
        maxRunning = Math.max(maxRunning, running);
        // Earlier calls take longer: only a sequential server keeps the order.
        await new Promise((resolve) => setTimeout(resolve, 20 - index));
        running -= 1;
        return okResult({ index }, index);
      },
    });
    const connection = await connectClient(server.advert);
    const finished: number[] = [];
    const results = await Promise.all(
      Array.from({ length: 20 }, (_, index) =>
        connection.callTool(callParams('get_drawing_info', { index })).then((result) => {
          finished.push(index);
          return result;
        }),
      ),
    );
    const order = Array.from({ length: 20 }, (_, index) => index);
    expect(started).toEqual(order);
    expect(finished).toEqual(order);
    expect(maxRunning).toBe(1);
    expect(results.map((result) => result.revision)).toEqual(order);
    expect(results.map((result) => (result.data as { index: number }).index)).toEqual(order);
  });

  it('serves two clients independently', async () => {
    const gate = deferred();
    const server = await startServer({
      callTool: async (params) => {
        if (params.name === 'slow') await gate.promise;
        return okResult({ name: params.name });
      },
    });
    const slowClient = await connectClient(server.advert);
    const fastClient = await connectClient(server.advert);
    const slow = slowClient.callTool(callParams('slow', {}));
    const fast = await fastClient.callTool(callParams('fast', {}));
    expect(fast.data).toEqual({ name: 'fast' });
    gate.resolve();
    expect((await slow).data).toEqual({ name: 'slow' });
  });

  it('maps a handler BridgeError to its code, message and data', async () => {
    const server = await startServer({
      callTool: async () => {
        throw new BridgeError(BridgeErrorCode.Busy, 'The renderer did not answer in 60 s', { waitedMs: 60000 });
      },
      hello: async () => {
        throw new BridgeError(BridgeErrorCode.NoActiveDocument, 'No drawing is open');
      },
    });
    const connection = await connectClient(server.advert);
    const busy = expectBridgeError(
      await rejection(connection.callTool(callParams('create_line', {}))),
      BridgeErrorCode.Busy,
      /^The renderer did not answer in 60 s$/,
    );
    expect(busy.data).toEqual({ waitedMs: 60000 });
    const noDocument = expectBridgeError(
      await rejection(connection.hello()),
      BridgeErrorCode.NoActiveDocument,
      /^No drawing is open$/,
    );
    expect(noDocument.data).toBeUndefined();
  });

  it('maps any other handler failure to InternalError with its message', async () => {
    let mode: 'error' | 'string' | 'reject' = 'error';
    const server = await startServer({
      callTool: async () => {
        if (mode === 'error') throw new TypeError('entity.layer is undefined');
        if (mode === 'string') throw 'plain string failure';
        return Promise.reject(new Error('rejected later'));
      },
      focusWindow: () => {
        throw new Error('synchronous throw');
      },
    });
    const connection = await connectClient(server.advert);
    expectBridgeError(
      await rejection(connection.callTool(callParams('x', {}))),
      BridgeErrorCode.InternalError,
      /^entity\.layer is undefined$/,
    );
    mode = 'string';
    expectBridgeError(
      await rejection(connection.callTool(callParams('x', {}))),
      BridgeErrorCode.InternalError,
      /^plain string failure$/,
    );
    mode = 'reject';
    expectBridgeError(
      await rejection(connection.callTool(callParams('x', {}))),
      BridgeErrorCode.InternalError,
      /^rejected later$/,
    );
    expectBridgeError(await rejection(connection.focusWindow()), BridgeErrorCode.InternalError, /^synchronous throw$/);
    // Failures do not poison the connection.
    expect(await connection.hello()).toEqual(HELLO);
  });

  it('reports a malformed ToolResult from a handler as InternalError', async () => {
    const server = await startServer({
      callTool: async () => ({ success: true }) as unknown as ToolResult,
    });
    const connection = await connectClient(server.advert);
    expectBridgeError(
      await rejection(connection.callTool(callParams('x', {}))),
      BridgeErrorCode.InternalError,
      /malformed tool result.*createdIds/,
    );
  });

  it('answers an unknown method with MethodNotFound', async () => {
    const server = await startServer();
    const client = await authedRawClient(server.advert);
    client.send({ jsonrpc: '2.0', id: 'm1', method: 'tools.list', params: {} });
    expect(await client.next()).toEqual({
      jsonrpc: '2.0',
      id: 'm1',
      error: { code: -32601, message: 'Unknown method "tools.list"' },
    });
    client.send({ jsonrpc: '2.0', id: 'm2', method: 'eval', params: { code: 'process.exit()' } });
    expect(await client.next()).toMatchObject({ id: 'm2', error: { code: -32601 } });
  });

  it('validates params and answers InvalidParams', async () => {
    const calls: unknown[] = [];
    const server = await startServer({
      callTool: async (params) => {
        calls.push(params);
        return okResult(null);
      },
      setWindowBounds: async (bounds) => {
        calls.push(bounds);
      },
    });
    const client = await authedRawClient(server.advert);
    const bad: [method: string, params: unknown][] = [
      ['tools.call', undefined],
      ['tools.call', {}],
      ['tools.call', { requestId: 'r', operationId: 'o', name: 'create_line', arguments: {} }],
      ['tools.call', { requestId: 'r', operationId: 'o', name: 'create_line', arguments: {}, source: 'robot' }],
      ['tools.call', { requestId: '', operationId: 'o', name: 'create_line', arguments: {}, source: 'ai' }],
      ['tools.call', { requestId: 'r', operationId: 'o', name: 7, arguments: {}, source: 'ai' }],
      ['tools.call', { requestId: 'r', operationId: 'o', name: 'n', arguments: {}, source: 'ai', expectedRevision: 1.5 }],
      ['tools.call', 'create_line'],
      ['window.setBounds', {}],
      ['window.setBounds', { bounds: { x: 0, y: 0, width: 0, height: 100 } }],
      ['window.setBounds', { bounds: { x: 0, y: 0, width: 100 } }],
      ['window.setBounds', { bounds: { x: '0', y: 0, width: 100, height: 100 } }],
      ['hello', 'text'],
      ['hello', [1]],
      ['window.focus', 3],
      ['window.getBounds', false],
    ];
    for (const [index, [method, params]] of bad.entries()) {
      client.send({ jsonrpc: '2.0', id: index, method, ...(params === undefined ? {} : { params }) });
      const reply = (await client.next()) as { id: number; error: { code: number; message: string } };
      expect(reply.id).toBe(index);
      expect(reply.error.code, `${method} ${JSON.stringify(params)}`).toBe(-32602);
      expect(reply.error.message).toContain(`Invalid params for ${method}`);
    }
    expect(calls).toEqual([]);

    // Missing or null params are fine for the methods that take none.
    client.send({ jsonrpc: '2.0', id: 'h1', method: 'hello' });
    expect(await client.next()).toMatchObject({ id: 'h1', result: { cad: 'cadstudio' } });
    client.send({ jsonrpc: '2.0', id: 'h2', method: 'hello', params: null });
    expect(await client.next()).toMatchObject({ id: 'h2', result: { cad: 'cadstudio' } });
  });

  it('answers malformed JSON with ParseError (id null) and keeps the connection', async () => {
    const server = await startServer();
    const client = await authedRawClient(server.advert);
    client.send('{"jsonrpc":"2.0","id":1,"method":\n');
    const reply = (await client.next()) as { id: null; error: { code: number; message: string } };
    expect(reply.id).toBeNull();
    expect(reply.error.code).toBe(-32700);
    expect(reply.error.message).toMatch(/^Parse error: /);
    client.send({ jsonrpc: '2.0', id: 2, method: 'hello' });
    expect(await client.next()).toMatchObject({ id: 2, result: { cad: 'cadstudio' } });
    expect(server.clientCount).toBe(1);
  });

  it('answers a non-request JSON value with InvalidRequest and ignores notifications', async () => {
    const server = await startServer();
    const client = await authedRawClient(server.advert);
    client.send('42\n');
    expect(await client.next()).toMatchObject({ id: null, error: { code: -32600 } });
    client.send({ id: 3, method: 'hello' });
    expect(await client.next()).toMatchObject({ id: null, error: { code: -32600 } });
    client.send({ jsonrpc: '2.0', method: 'some.notification', params: {} });
    client.send({ jsonrpc: '2.0', id: 99, result: 'stray response' });
    client.send({ jsonrpc: '2.0', id: 4, method: 'hello' });
    expect(await client.next()).toMatchObject({ id: 4, result: { cad: 'cadstudio' } });
    expect(client.received).toHaveLength(4);
  });

  it('accepts CRLF-terminated requests and several requests in one write', async () => {
    const server = await startServer();
    const client = await authedRawClient(server.advert);
    client.send(
      '{"jsonrpc":"2.0","id":"a","method":"hello"}\r\n\r\n{"jsonrpc":"2.0","id":"b","method":"window.getBounds"}\r\n',
    );
    expect(await client.next()).toMatchObject({ id: 'a', result: { cad: 'cadstudio' } });
    expect(await client.next()).toEqual({ jsonrpc: '2.0', id: 'b', result: null });
  });

  it('handles the window methods', async () => {
    let bounds: WindowBounds | null = null;
    let focused = 0;
    const server = await startServer({
      getWindowBounds: async () => bounds,
      setWindowBounds: async (next) => {
        bounds = next;
      },
      focusWindow: async () => {
        focused += 1;
      },
    });
    const connection = await connectClient(server.advert);
    expect(await connection.getWindowBounds()).toBeNull();
    expect(await connection.setWindowBounds({ x: -1920, y: 0, width: 1460, height: 1040 })).toBeUndefined();
    expect(bounds).toEqual({ x: -1920, y: 0, width: 1460, height: 1040 });
    expect(await connection.getWindowBounds()).toEqual({ x: -1920, y: 0, width: 1460, height: 1040 });
    expect(await connection.focusWindow()).toBeUndefined();
    expect(focused).toBe(1);
  });

  it('rejects malformed results from a connector on the client side', async () => {
    const server = await startServer({
      hello: async () => ({ cad: 'cadstudio' }) as unknown as HelloResult,
      getWindowBounds: async () => ({ x: 0, y: 0 }) as unknown as WindowBounds,
    });
    const connection = await connectClient(server.advert);
    expectBridgeError(await rejection(connection.hello()), BridgeErrorCode.InternalError, /malformed hello result/);
    expectBridgeError(
      await rejection(connection.getWindowBounds()),
      BridgeErrorCode.InternalError,
      /malformed window\.getBounds result/,
    );
  });
});

describe('notifications and client tracking', () => {
  it('broadcasts notifications to every authenticated client only', async () => {
    const server = await startServer();
    const first = await connectClient(server.advert);
    const second = await connectClient(server.advert);
    const stranger = await rawClient(server.advert.pipeName);
    const firstSeen: [string, unknown][] = [];
    const secondSeen: [string, unknown][] = [];
    first.onNotification((method, params) => firstSeen.push([method, params]));
    second.onNotification((method, params) => secondSeen.push([method, params]));

    server.notify('document.changed', { documentId: 'doc-1', revision: 8 });
    server.notify('selection.changed', { ids: ['a', 'b'] });
    server.notify('document.activated', { document: null });

    const expected: [string, unknown][] = [
      ['document.changed', { documentId: 'doc-1', revision: 8 }],
      ['selection.changed', { ids: ['a', 'b'] }],
      ['document.activated', { document: null }],
    ];
    await waitFor(() => firstSeen.length === 3 && secondSeen.length === 3);
    expect(firstSeen).toEqual(expected);
    expect(secondSeen).toEqual(expected);
    // Give a wrongly delivered message time to arrive before checking the unauthenticated socket.
    await first.hello();
    expect(stranger.received).toEqual([]);
  });

  it('keeps notifications in order with responses and supports unsubscribing', async () => {
    const server = await startServer({
      callTool: async () => {
        server.notify('document.changed', { documentId: 'doc-1', revision: 2 });
        return okResult(null, 2);
      },
    });
    const connection = await connectClient(server.advert);
    const events: string[] = [];
    const unsubscribe = connection.onNotification((method) => events.push(method));
    connection.onNotification(() => {
      throw new Error('a broken listener must not break the others');
    });
    const result = await connection.callTool(callParams('create_line', {}));
    events.push(`result:${result.revision}`);
    expect(events).toEqual(['document.changed', 'result:2']);

    unsubscribe();
    server.notify('selection.changed', { ids: [] });
    await connection.hello();
    expect(events).toEqual(['document.changed', 'result:2']);
  });

  it('reports the number of authenticated clients on every change', async () => {
    const counts: number[] = [];
    const server = await startServer({}, { onClientsChanged: (count) => counts.push(count) });
    expect(server.clientCount).toBe(0);

    const stranger = await rawClient(server.advert.pipeName);
    const first = await connectClient(server.advert);
    expect(counts).toEqual([1]);
    const second = await connectClient(server.advert);
    expect(counts).toEqual([1, 2]);
    expect(server.clientCount).toBe(2);

    first.close();
    await waitFor(() => counts.length === 3);
    expect(counts).toEqual([1, 2, 1]);

    stranger.socket.destroy();
    second.close();
    await waitFor(() => counts.length === 4);
    expect(counts).toEqual([1, 2, 1, 0]);
    expect(server.clientCount).toBe(0);

    // Failed logins never count.
    await rejection(connectToConnector({ ...server.advert, token: generateToken() }));
    expect(counts).toEqual([1, 2, 1, 0]);
  });

  it('survives an onClientsChanged callback that throws', async () => {
    const server = await startServer(
      {},
      {
        onClientsChanged: () => {
          throw new Error('listener bug');
        },
      },
    );
    const connection = await connectClient(server.advert);
    expect(await connection.hello()).toEqual(HELLO);
  });
});

describe('closing', () => {
  it('client close: fires onClose once, rejects later calls, frees the server slot', async () => {
    const server = await startServer();
    const connection = await connectClient(server.advert);
    const reasons: string[] = [];
    connection.onClose((reason) => reasons.push(reason));
    const removed = connection.onClose((reason) => reasons.push(`removed:${reason}`));
    removed();

    connection.close();
    connection.close();
    expect(reasons).toEqual(['closed by client']);
    await waitFor(() => server.clientCount === 0);

    const error = expectBridgeError(await rejection(connection.hello()), BridgeErrorCode.InternalError, /is closed \(closed by client\)/);
    expect(error.data).toEqual({ reason: 'connection-closed' });
    expectBridgeError(await rejection(connection.callTool(callParams('x', {}))), BridgeErrorCode.InternalError);

    // A late subscriber still learns the reason, exactly once.
    const late: string[] = [];
    connection.onClose((reason) => late.push(reason));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(late).toEqual(['closed by client']);
    expect(reasons).toEqual(['closed by client']);
  });

  it('client close rejects its pending calls and the server skips the queued ones', async () => {
    const gate = deferred();
    const handled: string[] = [];
    const server = await startServer({
      callTool: async (params) => {
        handled.push(params.name);
        await gate.promise;
        return okResult(null);
      },
    });
    const connection = await connectClient(server.advert);
    const first = rejection(connection.callTool(callParams('first', {})));
    const second = rejection(connection.callTool(callParams('second', {})));
    await waitFor(() => handled.length === 1);
    connection.close();
    expectBridgeError(await first, BridgeErrorCode.InternalError, /closed by client/);
    expectBridgeError(await second, BridgeErrorCode.InternalError, /closed by client/);
    await waitFor(() => server.clientCount === 0);
    gate.resolve();
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(handled).toEqual(['first']);
  });

  it('server close: notifies, closes clients with a reason, rejects pending calls, removes the advert', async () => {
    const gate = deferred();
    const counts: number[] = [];
    const server = await startServer(
      {
        callTool: async () => {
          await gate.promise;
          return okResult(null);
        },
      },
      { onClientsChanged: (count) => counts.push(count) },
    );
    const first = await connectClient(server.advert);
    const second = await connectClient(server.advert);
    const notifications: string[] = [];
    const reasons: string[] = [];
    first.onNotification((method) => notifications.push(method));
    first.onClose((reason) => reasons.push(`first:${reason}`));
    second.onClose((reason) => reasons.push(`second:${reason}`));
    const pending = rejection(first.callTool(callParams('slow', {})));

    const closing = server.close();
    expect(server.close()).toBe(closing);
    await closing;
    gate.resolve();

    const error = expectBridgeError(await pending, BridgeErrorCode.InternalError, /is closed \(connector closing\)/);
    expect(error.data).toEqual({ reason: 'connection-closed' });
    await waitFor(() => reasons.length === 2);
    expect(reasons.sort()).toEqual(['first:connector closing', 'second:connector closing']);
    expect(notifications).toEqual(['connector.closing']);
    expect(server.clientCount).toBe(0);
    expect(counts).toEqual([1, 2, 1, 0]);
    expect(advertFiles()).toEqual([]);
    expect(await listAdverts()).toEqual([]);

    // Nothing listens any more, and notify() after close is a harmless no-op.
    server.notify('document.changed', { documentId: 'd', revision: 1 });
    expectBridgeError(
      await rejection(connectToConnector(server.advert)),
      BridgeErrorCode.InternalError,
      /Could not connect to CAD Studio \(test\)/,
    );
    expectBridgeError(await rejection(first.hello()), BridgeErrorCode.InternalError, /connector closing/);
  });

  it('reports a dropped connection as "connection lost"', async () => {
    // A peer that authenticates and then dies without a goodbye (the CAD process crashed).
    const pipeName = pipeNameFor('autocad', 777_003);
    const accepted: Socket[] = [];
    const crashing = createServer((socket) => {
      accepted.push(socket);
      socket.setEncoding('utf8');
      socket.on('error', () => undefined);
      socket.on('data', (chunk: string | Buffer) => {
        const request = JSON.parse(String(chunk)) as { id: number; method: string };
        if (request.method === 'auth') socket.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, result: { ok: true } })}\n`);
      });
    });
    rawServers.push(crashing);
    await new Promise<void>((resolve) => crashing.listen(pipeName, () => resolve()));
    const advert: ConnectorAdvert = {
      id: 'autocad-777003',
      cad: 'autocad',
      displayName: 'Crashing CAD',
      cadVersion: '1',
      connectorVersion: '1',
      protocolVersion: 1,
      pipeName,
      token: generateToken(),
      pid: process.pid,
      startedAt: new Date().toISOString(),
    };
    const connection = await connectClient(advert);
    const reasons: string[] = [];
    connection.onClose((reason) => reasons.push(reason));
    const pending = rejection(connection.callTool(callParams('never-answered', {})));
    await waitFor(() => accepted.length === 1);
    for (const socket of accepted) socket.destroy();

    const error = expectBridgeError(await pending, BridgeErrorCode.InternalError, /The connection to Crashing CAD is closed/);
    expect(error.data).toEqual({ reason: 'connection-closed' });
    await waitFor(() => reasons.length === 1);
    expect(reasons[0]).toMatch(/^connection (lost|error: .+)$/);
  });

  it('can restart on the same pipe after closing', async () => {
    const first = await startServer();
    const pipeName = first.advert.pipeName;
    await first.close();
    const second = await startServer();
    expect(second.advert.pipeName).toBe(pipeName);
    const connection = await connectClient(second.advert);
    expect(await connection.hello()).toEqual(HELLO);
  });
});

describe('timeouts and cancellation', () => {
  it('rejects a slow tool call with Busy after timeoutMs and ignores the late response', async () => {
    const gate = deferred();
    let calls = 0;
    const server = await startServer({
      callTool: async (params) => {
        calls += 1;
        if (params.name === 'slow') await gate.promise;
        return okResult({ name: params.name });
      },
    });
    const connection = await connectClient(server.advert);
    const startedAt = Date.now();
    const error = expectBridgeError(
      await rejection(connection.callTool(callParams('slow', {}), { timeoutMs: 80 })),
      BridgeErrorCode.Busy,
      /CAD Studio \(test\) did not answer tools\.call within 80 ms/,
    );
    expect(error.data).toEqual({ reason: 'timeout' });
    expect(Date.now() - startedAt).toBeLessThan(2000);

    // The late answer arrives now and must be dropped without disturbing the next call.
    gate.resolve();
    const next = await connection.callTool(callParams('fast', {}));
    expect(next.data).toEqual({ name: 'fast' });
    expect(calls).toBe(2);
  });

  it('rejects with Busy when the AbortSignal fires and keeps the connection usable', async () => {
    const gate = deferred();
    const server = await startServer({
      callTool: async (params) => {
        if (params.name === 'slow') await gate.promise;
        return okResult({ name: params.name });
      },
    });
    const connection = await connectClient(server.advert);
    const controller = new AbortController();
    const pending = rejection(connection.callTool(callParams('slow', {}), { signal: controller.signal }));
    setTimeout(() => controller.abort(), 30);
    const error = expectBridgeError(await pending, BridgeErrorCode.Busy, /tools\.call was cancelled/);
    expect(error.data).toEqual({ reason: 'aborted' });

    gate.resolve();
    expect((await connection.callTool(callParams('fast', {}))).data).toEqual({ name: 'fast' });
  });

  it('does not send a call whose signal is already aborted', async () => {
    let calls = 0;
    const server = await startServer({
      callTool: async () => {
        calls += 1;
        return okResult(null);
      },
    });
    const connection = await connectClient(server.advert);
    const controller = new AbortController();
    controller.abort();
    expectBridgeError(
      await rejection(connection.callTool(callParams('x', {}), { signal: controller.signal })),
      BridgeErrorCode.Busy,
      /cancelled before it was sent/,
    );
    await connection.hello();
    expect(calls).toBe(0);
  });

  it('an abort after completion is harmless', async () => {
    const server = await startServer();
    const connection = await connectClient(server.advert);
    const controller = new AbortController();
    const result = await connection.callTool(callParams('x', { a: 1 }), { signal: controller.signal, timeoutMs: 1000 });
    controller.abort();
    expect(result.data).toEqual({ echo: { a: 1 } });
    expect(await connection.hello()).toEqual(HELLO);
  });

  it('rejects quickly when nothing listens on the pipe', async () => {
    const advert: ConnectorAdvert = {
      id: 'autocad-1',
      cad: 'autocad',
      displayName: 'AutoCAD 2026',
      cadVersion: '25.1',
      connectorVersion: '0.1.0',
      protocolVersion: 1,
      pipeName: pipeNameFor('autocad', 999_999),
      token: generateToken(),
      pid: 999_999,
      startedAt: new Date().toISOString(),
    };
    const startedAt = Date.now();
    const error = expectBridgeError(
      await rejection(connectToConnector(advert)),
      BridgeErrorCode.InternalError,
      /^Could not connect to AutoCAD 2026 \(/,
    );
    expect(error.data).toEqual({ reason: 'connect-failed' });
    expect(Date.now() - startedAt).toBeLessThan(3000);
  });

  it('times out with Busy when the peer accepts but never answers auth', async () => {
    const pipeName = pipeNameFor('autocad', 777_001);
    const accepted: Socket[] = [];
    const silent = createServer((socket) => {
      accepted.push(socket);
      socket.on('error', () => undefined);
    });
    rawServers.push(silent);
    await new Promise<void>((resolve) => silent.listen(pipeName, () => resolve()));
    const advert: ConnectorAdvert = {
      id: 'autocad-777001',
      cad: 'autocad',
      displayName: '',
      cadVersion: '25.1',
      connectorVersion: '0.1.0',
      protocolVersion: 1,
      pipeName,
      token: generateToken(),
      pid: process.pid,
      startedAt: new Date().toISOString(),
    };
    const error = expectBridgeError(
      await rejection(connectToConnector(advert, { connectTimeoutMs: 150 })),
      BridgeErrorCode.Busy,
      /^Timed out connecting to autocad-777001 after 150 ms$/,
    );
    expect(error.data).toEqual({ reason: 'timeout' });
    for (const socket of accepted) socket.destroy();
  });

  it('fails with a clear error when the peer hangs up during auth', async () => {
    const pipeName = pipeNameFor('autocad', 777_002);
    const rude = createServer((socket) => {
      socket.on('error', () => undefined);
      socket.destroy();
    });
    rawServers.push(rude);
    await new Promise<void>((resolve) => rude.listen(pipeName, () => resolve()));
    const advert: ConnectorAdvert = {
      id: 'autocad-777002',
      cad: 'autocad',
      displayName: 'Rude CAD',
      cadVersion: '1',
      connectorVersion: '1',
      protocolVersion: 1,
      pipeName,
      token: generateToken(),
      pid: process.pid,
      startedAt: new Date().toISOString(),
    };
    const error = await rejection(connectToConnector(advert, { connectTimeoutMs: 2000 }));
    expect(error).toBeInstanceOf(BridgeError);
    expect((error as BridgeError).message).toMatch(/Rude CAD/);
  });

  it('times out hello with the request timeout', async () => {
    const gate = deferred();
    const server = await startServer({
      hello: async () => {
        await gate.promise;
        return HELLO;
      },
    });
    const connection = await connectToConnector(server.advert, { requestTimeoutMs: 60 });
    connections.push(connection);
    expectBridgeError(await rejection(connection.hello()), BridgeErrorCode.Busy, /did not answer hello within 60 ms/);
    gate.resolve();
  });
});
