#!/usr/bin/env node
// Launch an Electron main script with ELECTRON_RUN_AS_NODE removed (VS Code sets it,
// and then electron.exe behaves like plain Node). Waits for Electron to exit and
// returns its exit code, so it works the same from PowerShell, cmd and bash.
//
//   node tools/run-electron.js record.js --url ../index.html --out out.mp4
//   node tools/run-electron.js verify.js out.mp4 --frames 1,5,9 --outdir frames
//
// A script name without a directory is looked up next to this file first.
// Set ELECTRON_EXE to override which electron binary is used.
'use strict';
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

function findElectron() {
  const fromEnv = process.env.ELECTRON_EXE;
  if (fromEnv && fs.existsSync(fromEnv)) return fromEnv;
  const exeName = process.platform === 'win32' ? 'electron.exe' : 'electron';
  for (let dir = __dirname; ; dir = path.dirname(dir)) {
    const exe = path.join(dir, 'node_modules', 'electron', 'dist', exeName);
    if (fs.existsSync(exe)) return exe;
    if (path.dirname(dir) === dir) break;
  }
  throw new Error('electron binary not found (looked for node_modules/electron/dist/' + exeName + ' above ' + __dirname + '); set ELECTRON_EXE');
}

const [scriptArg, ...rest] = process.argv.slice(2);
if (!scriptArg) {
  console.error('usage: node run-electron.js <script.js> [args...]');
  process.exit(2);
}
let script = path.resolve(scriptArg);
if (!fs.existsSync(script) && fs.existsSync(path.join(__dirname, scriptArg))) script = path.join(__dirname, scriptArg);

const env = {};
for (const [k, v] of Object.entries(process.env)) {
  if (k.toUpperCase() !== 'ELECTRON_RUN_AS_NODE') env[k] = v;
}

const child = spawn(findElectron(), [script, ...rest], { stdio: 'inherit', env, windowsHide: true });
const forward = (sig) => () => { try { child.kill(sig); } catch { /* already gone */ } };
process.on('SIGINT', forward('SIGINT'));
process.on('SIGTERM', forward('SIGTERM'));
child.on('error', (e) => { console.error('failed to start electron: ' + e.message); process.exit(1); });
child.on('exit', (code, signal) => process.exit(code == null ? (signal ? 1 : 0) : code));
