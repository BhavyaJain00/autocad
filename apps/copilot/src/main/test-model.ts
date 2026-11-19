/**
 * The scripted test model (end-to-end tests and offline demos): replaces Claude only when the
 * environment asks for it explicitly with BOTH `CADCOPILOT_E2E=1` and `CADCOPILOT_SCRIPT=<path of
 * a JSON file>`. It is never a default and the UI labels it "TEST MODEL". No Electron imports.
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { ModelTransport } from '@cadstudio/copilot-agent';
import { ENV_E2E, ENV_SCRIPT } from './constants';
import { errorMessage } from './json-file';

/** Largest script file read. */
export const SCRIPT_MAX_BYTES = 8 * 1024 * 1024;

export interface TestModelOptions {
  readonly env: Readonly<Record<string, string | undefined>>;
  /** `createScriptedTransport` of @cadstudio/copilot-agent. */
  readonly createTransport: (script: unknown) => ModelTransport;
  /** Reads the script file (tests). Defaults to reading it from disk. */
  readonly readScript?: (filePath: string) => string;
}

/** The script path when the environment asks for the scripted test model, otherwise null. */
export function requestedScriptPath(env: Readonly<Record<string, string | undefined>>): string | null {
  if (env[ENV_E2E] !== '1') return null;
  const script = env[ENV_SCRIPT]?.trim();
  return script !== undefined && script.length > 0 ? path.resolve(script) : null;
}

function readScriptFile(filePath: string): string {
  const buffer = readFileSync(filePath);
  if (buffer.byteLength > SCRIPT_MAX_BYTES) throw new Error(`the file is larger than ${SCRIPT_MAX_BYTES} bytes`);
  return buffer.toString('utf8');
}

/**
 * The scripted transport when it was requested, otherwise null (the real model is used). Throws
 * when a script was requested but cannot be loaded: silently falling back to the real model in a
 * test run would send test prompts to the API.
 */
export function loadTestModel(options: TestModelOptions): ModelTransport | null {
  const scriptPath = requestedScriptPath(options.env);
  if (scriptPath === null) return null;
  let script: unknown;
  try {
    let text = (options.readScript ?? readScriptFile)(scriptPath);
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    script = JSON.parse(text) as unknown;
  } catch (error) {
    throw new Error(`The test model script "${scriptPath}" could not be read: ${errorMessage(error)}`);
  }
  try {
    return options.createTransport(script);
  } catch (error) {
    throw new Error(`The test model script "${scriptPath}" is not valid: ${errorMessage(error)}`);
  }
}
