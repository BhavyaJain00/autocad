// Development pipeline for CAD Copilot (`npm run dev -w @cadstudio/copilot`):
//   1. Vite dev server for the renderer (hot reload) on http://127.0.0.1:5174
//   2. esbuild in watch mode for the main process and the preload script
//   3. Electron, pointed at the dev server, restarted whenever main/preload are rebuilt
//
// Everything stops together: Ctrl+C here, or closing the app window, shuts down the dev server,
// the watcher and the whole Electron process tree.
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import process from 'node:process';
import { createServer } from 'vite';
import { createElectronBuildContext, copilotDir, electronOutDir } from './build-electron.mjs';

const DEV_SERVER_PORT = 5174;
/** Quiet time after a rebuild before Electron restarts (editors often save several files at once). */
const RESTART_DEBOUNCE_MS = 200;
/** Longest the shutdown may take before the process exits anyway. */
const SHUTDOWN_DEADLINE_MS = 8000;

/** In plain Node, the `electron` package exports the path of the Electron binary. */
const electronBinary = /** @type {string} */ (createRequire(import.meta.url)('electron'));

/** @type {import('vite').ViteDevServer | undefined} */
let viteServer;
/** @type {import('esbuild').BuildContext | undefined} */
let buildContext;
/** @type {import('node:child_process').ChildProcess | null} */
let electronProcess = null;
/** Electron processes this script is stopping on purpose (their exit must not end the session). */
const stoppedOnPurpose = new WeakSet();
let devServerUrl = `http://127.0.0.1:${DEV_SERVER_PORT}/`;
let startedOnce = false;
/** Fingerprint of the bundles Electron is running; a rebuild with the same output needs no restart. */
let runningFingerprint = '';
let shuttingDown = false;
/** @type {ReturnType<typeof setTimeout> | undefined} */
let restartTimer;
/** Serialises restarts so two rebuilds never race two Electron instances. */
let restartQueue = Promise.resolve();

function log(message) {
  console.log(`[dev] ${message}`);
}

/** This process's environment for Electron: the dev server URL in, ELECTRON_RUN_AS_NODE out. */
function electronEnvironment() {
  /** @type {NodeJS.ProcessEnv} */
  const env = {};
  for (const [name, value] of Object.entries(process.env)) {
    // ELECTRON_RUN_AS_NODE (set by VS Code terminals and some tools) would start Electron as plain Node.
    if (value !== undefined && name.toUpperCase() !== 'ELECTRON_RUN_AS_NODE') env[name] = value;
  }
  env.VITE_DEV_SERVER_URL = devServerUrl;
  return env;
}

function hasExited(child) {
  return child.exitCode !== null || child.signalCode !== null;
}

/**
 * Stops a process and everything it started; resolves once it has exited. On Windows a plain
 * kill() would leave Electron's helper processes behind, so the whole tree is taken down.
 * @param {import('node:child_process').ChildProcess} child
 * @returns {Promise<void>}
 */
function killTree(child) {
  return new Promise((resolve) => {
    if (hasExited(child) || child.pid === undefined) {
      resolve();
      return;
    }
    child.once('exit', () => resolve());
    if (process.platform === 'win32') {
      const killer = spawn('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
      killer.once('error', () => child.kill());
      return;
    }
    child.kill('SIGTERM');
    const escalate = setTimeout(() => {
      if (!hasExited(child)) child.kill('SIGKILL');
    }, 4000);
    child.once('exit', () => clearTimeout(escalate));
  });
}

function startElectron() {
  if (shuttingDown) return;
  const child = spawn(electronBinary, [copilotDir], { cwd: copilotDir, env: electronEnvironment(), stdio: 'inherit' });
  electronProcess = child;
  child.once('error', (error) => {
    console.error(`[dev] Electron could not be started: ${error.message}`);
    void shutdown(1);
  });
  child.once('exit', (code) => {
    if (electronProcess === child) electronProcess = null;
    if (shuttingDown || stoppedOnPurpose.has(child)) return;
    // The app was closed (or crashed): the dev session ends with it.
    log(`Electron exited${code === null ? '' : ` with code ${code}`}; stopping the dev server.`);
    void shutdown(code ?? 0);
  });
}

async function restartElectron() {
  if (shuttingDown) return;
  const child = electronProcess;
  if (child !== null) {
    stoppedOnPurpose.add(child);
    await killTree(child);
  }
  startElectron();
}

/** Hash of the built main + preload bundles. */
async function bundleFingerprint() {
  const hash = createHash('sha256');
  for (const name of ['main.cjs', 'preload.cjs']) {
    hash.update(await readFile(path.join(electronOutDir, name)));
  }
  return hash.digest('hex');
}

/** Runs after the initial build and after every rebuild of main/preload. */
async function onElectronBuild(result) {
  if (shuttingDown) return;
  if (result.errors.length > 0) {
    log(
      startedOnce
        ? 'main/preload build failed; Electron keeps running the previous build.'
        : 'main/preload build failed; Electron starts after the next successful build.',
    );
    return;
  }
  const fingerprint = await bundleFingerprint();
  if (shuttingDown) return;
  if (!startedOnce) {
    startedOnce = true;
    runningFingerprint = fingerprint;
    log('main + preload built; starting Electron.');
    startElectron();
    return;
  }
  // esbuild also rebuilds when unrelated files appear next to the sources; same output, no restart.
  if (fingerprint === runningFingerprint) return;
  runningFingerprint = fingerprint;
  clearTimeout(restartTimer);
  restartTimer = setTimeout(() => {
    log('main/preload rebuilt; restarting Electron.');
    restartQueue = restartQueue.then(restartElectron).catch((error) => {
      console.error('[dev] Restarting Electron failed:', error);
    });
  }, RESTART_DEBOUNCE_MS);
}

/**
 * Stops Electron, the watcher and the dev server, then exits. Safe to call more than once.
 * @param {number} exitCode
 */
async function shutdown(exitCode) {
  if (shuttingDown) return;
  shuttingDown = true;
  clearTimeout(restartTimer);
  // Never hang on a stuck child or socket.
  setTimeout(() => process.exit(exitCode), SHUTDOWN_DEADLINE_MS).unref();
  const child = electronProcess;
  await Promise.allSettled([
    child === null ? undefined : killTree(child),
    buildContext?.dispose(),
    viteServer?.close(),
  ]);
  process.exit(exitCode);
}

async function main() {
  // SIGBREAK is Ctrl+Break on Windows; SIGHUP is a closed terminal.
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']) {
    process.on(signal, () => {
      void shutdown(0);
    });
  }
  process.on('uncaughtException', (error) => {
    console.error('[dev] Unexpected error:', error);
    void shutdown(1);
  });

  viteServer = await createServer({
    configFile: path.join(copilotDir, 'vite.config.ts'),
    server: { port: DEV_SERVER_PORT, strictPort: true },
  });
  await viteServer.listen();
  devServerUrl = viteServer.resolvedUrls?.local[0] ?? devServerUrl;
  log(`renderer dev server: ${devServerUrl}`);

  buildContext = await createElectronBuildContext({
    onBuild(result) {
      onElectronBuild(result).catch((error) => {
        console.error('[dev] Handling the main/preload build failed:', error);
      });
    },
  });
  await buildContext.watch();
  log('watching src/main, src/preload and src/shared (Ctrl+C to stop)');
}

main().catch((error) => {
  console.error(`[dev] ${error instanceof Error ? error.message : String(error)}`);
  void shutdown(1);
});
