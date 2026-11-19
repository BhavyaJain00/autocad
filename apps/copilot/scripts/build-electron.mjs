// Bundles the Electron main process and the preload script with esbuild:
//   src/main/main.ts       -> dist/electron/main.cjs
//   src/preload/preload.ts -> dist/electron/preload.cjs
//
// Usage: node scripts/build-electron.mjs [--watch]
// scripts/dev.mjs imports createElectronBuildContext() to rebuild on change.
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import * as esbuild from 'esbuild';

export const copilotDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const electronOutDir = path.join(copilotDir, 'dist', 'electron');

/**
 * esbuild options shared by the one-off build and the watch context. Both bundles are CommonJS
 * (the preload runs sandboxed, where only `require('electron')` is available).
 * @param {esbuild.Plugin[]} [plugins]
 * @returns {esbuild.BuildOptions}
 */
export function electronBuildOptions(plugins = []) {
  return {
    absWorkingDir: copilotDir,
    entryPoints: {
      main: 'src/main/main.ts',
      preload: 'src/preload/preload.ts',
    },
    outdir: electronOutDir,
    outExtension: { '.js': '.cjs' },
    bundle: true,
    platform: 'node',
    format: 'cjs',
    target: 'node22',
    external: ['electron'],
    sourcemap: true,
    tsconfig: path.join(copilotDir, 'tsconfig.node.json'),
    logLevel: 'warning',
    plugins,
  };
}

/**
 * Creates an esbuild context for watch mode. `onBuild` runs after the initial build and after every
 * rebuild with esbuild's result (check `result.errors.length`).
 * @param {{ onBuild?: (result: esbuild.BuildResult) => void }} [options]
 * @returns {Promise<esbuild.BuildContext>}
 */
export async function createElectronBuildContext(options = {}) {
  /** @type {esbuild.Plugin} */
  const notify = {
    name: 'cadcopilot-build-notify',
    setup(build) {
      build.onEnd((result) => {
        options.onBuild?.(result);
      });
    },
  };
  return esbuild.context(electronBuildOptions([notify]));
}

async function runCli() {
  const watch = process.argv.includes('--watch');
  if (!watch) {
    const startedAt = Date.now();
    await esbuild.build(electronBuildOptions());
    const relative = path.relative(process.cwd(), electronOutDir) || '.';
    console.log(`electron: built main.cjs + preload.cjs into ${relative} in ${Date.now() - startedAt} ms`);
    return;
  }
  const context = await createElectronBuildContext({
    onBuild(result) {
      const status = result.errors.length === 0 ? 'rebuilt' : `failed with ${result.errors.length} error(s)`;
      console.log(`electron: main + preload ${status} (${new Date().toLocaleTimeString()})`);
    },
  });
  await context.watch();
  console.log('electron: watching src/main, src/preload and src/shared (Ctrl+C to stop)');
  const stop = () => {
    void context.dispose().finally(() => process.exit(0));
  };
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);
}

const invokedDirectly = process.argv[1] !== undefined && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (invokedDirectly) {
  runCli().catch((error) => {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  });
}
