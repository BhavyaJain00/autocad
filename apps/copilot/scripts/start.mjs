// `npm start` for CAD Copilot: runs the built app (dist/) with this repository's Electron.
// A plain `electron .` breaks when ELECTRON_RUN_AS_NODE is set (VS Code terminals and some tools
// set it), because Electron then starts as plain Node; this launcher removes that variable and a
// stray VITE_DEV_SERVER_URL before starting Electron.
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const appDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const REMOVED = new Set(['ELECTRON_RUN_AS_NODE', 'VITE_DEV_SERVER_URL']);

if (!existsSync(path.join(appDir, 'dist', 'electron', 'main.cjs'))) {
  console.error('CAD Copilot is not built yet. Run "npm run build:copilot" first.');
  process.exit(1);
}

/** In plain Node, the `electron` package exports the path of the Electron binary. */
const electronBinary = /** @type {string} */ (createRequire(import.meta.url)('electron'));

/** @type {Record<string, string>} */
const env = {};
for (const [name, value] of Object.entries(process.env)) {
  if (value !== undefined && !REMOVED.has(name.toUpperCase())) env[name] = value;
}

const child = spawn(electronBinary, [appDir, ...process.argv.slice(2)], { cwd: appDir, env, stdio: 'inherit' });
child.once('error', (error) => {
  console.error(`CAD Copilot could not be started: ${error.message}`);
  process.exit(1);
});
child.once('exit', (code, signal) => process.exit(code ?? (signal ? 1 : 0)));
// Ctrl+C reaches Electron through the shared console; keep this launcher alive until it exits.
process.on('SIGINT', () => undefined);
