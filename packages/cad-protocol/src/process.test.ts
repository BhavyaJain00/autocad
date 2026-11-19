// Cross-process checks: the package's entry points are bundled with esbuild into a temp folder
// and a real child process hosts a connector that this process discovers and talks to.
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { build } from 'esbuild';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BridgeError, BridgeErrorCode } from './index';
import type { ConnectorAdvert } from './index';
import { connectToConnector, connectorsDirectory, listAdverts } from './node';

const HOME = mkdtempSync(join(tmpdir(), 'cadcopilot-process-'));
process.env.CADCOPILOT_HOME = HOME;

const SOURCE_DIR = fileURLToPath(new URL('.', import.meta.url));
const BUILD_DIR = join(HOME, 'build');
const NODE_BUNDLE = join(BUILD_DIR, 'node-entry.mjs');
const CHILD_SCRIPT = join(BUILD_DIR, 'child.mjs');

const children: ChildProcess[] = [];

const CHILD_SOURCE = `
import { startConnectorServer } from ${JSON.stringify(pathToFileURL(NODE_BUNDLE).href)};

const server = await startConnectorServer({
  cad: 'cadstudio',
  displayName: 'Child CAD',
  cadVersion: '2.0.0',
  connectorVersion: '0.1.0',
  handlers: {
    hello: async () => ({
      cad: 'cadstudio',
      cadVersion: '2.0.0',
      connectorVersion: '0.1.0',
      protocolVersion: 1,
      document: null,
      supportedTools: ['get_drawing_revision'],
    }),
    callTool: async (params) => ({
      success: true,
      data: { pid: process.pid, name: params.name, arguments: params.arguments },
      createdIds: [],
      modifiedIds: [],
      deletedIds: [],
      warnings: [],
      errors: [],
      revision: 1,
    }),
    getWindowBounds: async () => ({ x: 10, y: 20, width: 800, height: 600 }),
    setWindowBounds: async () => undefined,
    focusWindow: async () => undefined,
  },
});

process.stdout.write('READY ' + JSON.stringify(server.advert) + '\\n');
process.stdin.setEncoding('utf8');
process.stdin.on('data', (text) => {
  if (text.includes('exit')) process.exit(0);
  if (text.includes('close')) void server.close().then(() => process.exit(0));
  if (text.includes('notify')) server.notify('document.changed', { documentId: 'child', revision: 2 });
});
`;

beforeAll(async () => {
  await build({
    entryPoints: [join(SOURCE_DIR, 'node.ts')],
    outfile: NODE_BUNDLE,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node20',
    logLevel: 'silent',
  });
  writeFileSync(CHILD_SCRIPT, CHILD_SOURCE);
}, 60_000);

afterAll(() => {
  for (const child of children) child.kill();
  rmSync(HOME, { recursive: true, force: true });
});

/** Starts the connector child and resolves with its advert once it reports READY. */
async function startChild(): Promise<{ child: ChildProcess; advert: ConnectorAdvert; exited: Promise<number | null> }> {
  const child = spawn(process.execPath, [CHILD_SCRIPT], {
    env: { ...process.env, CADCOPILOT_HOME: HOME },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  children.push(child);
  const exited = new Promise<number | null>((resolve) => child.on('exit', (code) => resolve(code)));
  const advert = await new Promise<ConnectorAdvert>((resolve, reject) => {
    let output = '';
    let errors = '';
    child.stdout?.setEncoding('utf8');
    child.stderr?.setEncoding('utf8');
    child.stderr?.on('data', (text: string) => {
      errors += text;
    });
    child.stdout?.on('data', (text: string) => {
      output += text;
      const line = output.split('\n').find((candidate) => candidate.startsWith('READY '));
      if (line !== undefined) resolve(JSON.parse(line.slice('READY '.length)) as ConnectorAdvert);
    });
    child.on('exit', (code) => reject(new Error(`child exited early with code ${String(code)}: ${errors}`)));
  });
  return { child, advert, exited };
}

async function waitFor(condition: () => boolean, timeoutMs = 5000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) throw new Error('waitFor timed out');
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

describe('browser safety', () => {
  it('bundles the main entry point for a browser without any Node built-in', async () => {
    const result = await build({
      entryPoints: [join(SOURCE_DIR, 'index.ts')],
      bundle: true,
      platform: 'browser',
      format: 'esm',
      write: false,
      metafile: true,
      logLevel: 'silent',
    });
    expect(result.errors).toEqual([]);
    const inputs = Object.keys(result.metafile.inputs);
    expect(inputs.some((input) => input.endsWith('src/index.ts'))).toBe(true);
    expect(inputs.some((input) => input.endsWith('src/node.ts'))).toBe(false);
    const imported = Object.values(result.metafile.inputs).flatMap((input) => input.imports.map((entry) => entry.path));
    expect(imported.filter((path) => path.startsWith('node:'))).toEqual([]);
    const code = result.outputFiles[0]?.text ?? '';
    expect(code.length).toBeGreaterThan(10_000);
    expect(code).not.toMatch(/from\s*["']node:/);
    expect(code).not.toMatch(/require\(["'](node:)?(fs|net|os|path|crypto)["']\)/);
  }, 60_000);

  it('cannot bundle the Node entry point for a browser (it needs net, fs, crypto)', async () => {
    await expect(
      build({
        entryPoints: [join(SOURCE_DIR, 'node.ts')],
        bundle: true,
        platform: 'browser',
        format: 'esm',
        write: false,
        logLevel: 'silent',
      }),
    ).rejects.toThrow(/node:(net|fs|crypto|os|path)/);
  }, 60_000);
});

describe('a connector in another process', () => {
  it('is discovered, serves requests and removes its advert when the process exits without closing', async () => {
    const { child, advert, exited } = await startChild();
    expect(advert.pid).toBe(child.pid);
    expect(advert.id).toBe(`cadstudio-${String(child.pid)}`);
    expect(advert.pid).not.toBe(process.pid);

    const adverts = await listAdverts();
    expect(adverts).toEqual([advert]);

    const connection = await connectToConnector(advert, { clientName: 'vitest' });
    const hello = await connection.hello();
    expect(hello).toMatchObject({ cad: 'cadstudio', cadVersion: '2.0.0', supportedTools: ['get_drawing_revision'] });
    const result = await connection.callTool({
      requestId: 'r1',
      operationId: 'o1',
      name: 'get_drawing_revision',
      arguments: { nested: [1, 2, { three: 3 }] },
      source: 'user',
    });
    expect(result.data).toEqual({
      pid: child.pid,
      name: 'get_drawing_revision',
      arguments: { nested: [1, 2, { three: 3 }] },
    });
    expect(await connection.getWindowBounds()).toEqual({ x: 10, y: 20, width: 800, height: 600 });

    const notifications: [string, unknown][] = [];
    connection.onNotification((method, params) => notifications.push([method, params]));
    child.stdin?.write('notify\n');
    await waitFor(() => notifications.length === 1);
    expect(notifications).toEqual([['document.changed', { documentId: 'child', revision: 2 }]]);

    // The CAD process exits without calling close(): the exit hook must still remove the advert,
    // and the client must notice the lost connection.
    const reasons: string[] = [];
    connection.onClose((reason) => reasons.push(reason));
    child.stdin?.write('exit\n');
    expect(await exited).toBe(0);
    await waitFor(() => reasons.length === 1);
    expect(reasons[0]).toMatch(/^connection (lost|error: .+)$/);
    expect(existsSync(join(connectorsDirectory(), `${advert.id}.json`))).toBe(false);
    expect(await listAdverts()).toEqual([]);

    const error = await connection.hello().catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(BridgeError);
    expect((error as BridgeError).code).toBe(BridgeErrorCode.InternalError);
  }, 30_000);

  it('announces a graceful close to its clients', async () => {
    const { child, advert, exited } = await startChild();
    const connection = await connectToConnector(advert);
    const notifications: string[] = [];
    const reasons: string[] = [];
    connection.onNotification((method) => notifications.push(method));
    connection.onClose((reason) => reasons.push(reason));
    child.stdin?.write('close\n');
    expect(await exited).toBe(0);
    await waitFor(() => reasons.length === 1);
    expect(reasons).toEqual(['connector closing']);
    expect(notifications).toEqual(['connector.closing']);
    expect(readdirSync(connectorsDirectory())).toEqual([]);
  }, 30_000);

  it('cleans up the advert of a connector that was killed', async () => {
    const { child, advert, exited } = await startChild();
    const file = join(connectorsDirectory(), `${advert.id}.json`);
    expect(existsSync(file)).toBe(true);
    child.kill('SIGKILL');
    await exited;
    // A killed process cannot run its exit hook: the stale advert is still there...
    expect(existsSync(file)).toBe(true);
    // ...until the next discovery pass notices the pid is gone.
    expect(await listAdverts()).toEqual([]);
    expect(existsSync(file)).toBe(false);
    const error = await connectToConnector(advert).catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(BridgeError);
    expect((error as BridgeError).message).toMatch(/^Could not connect to Child CAD/);
  }, 30_000);
});
