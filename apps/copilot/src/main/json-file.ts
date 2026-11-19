/**
 * Small JSON files in the userData folder (settings, credentials): tolerant reads and atomic
 * writes (temp file + fsync + rename). No Electron imports.
 */
import { randomBytes } from 'node:crypto';
import { mkdirSync, readFileSync } from 'node:fs';
import * as fsp from 'node:fs/promises';
import path from 'node:path';

/** Largest settings / credentials file read (anything bigger is not one of ours). */
export const MAX_JSON_FILE_BYTES = 1024 * 1024;

/** Delays before each retry of a rename that failed with a transient Windows lock error. */
export const RENAME_RETRY_DELAYS_MS: readonly number[] = [50, 100, 200, 400];

const RETRYABLE_RENAME_CODES: ReadonlySet<string> = new Set(['EPERM', 'EBUSY', 'EACCES']);

export type JsonReadResult =
  | { readonly status: 'ok'; readonly value: unknown }
  | { readonly status: 'missing' }
  | { readonly status: 'invalid'; readonly reason: string };

function errnoCode(error: unknown): string | undefined {
  if (typeof error !== 'object' || error === null) return undefined;
  const code = (error as { code?: unknown }).code;
  return typeof code === 'string' ? code : undefined;
}

/** Message of an unknown thrown value. */
export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * Reads and parses a JSON file. Never throws: a missing file is 'missing'; an unreadable,
 * oversized or malformed one is 'invalid' with the reason.
 */
export function readJsonFile(filePath: string): JsonReadResult {
  let text: string;
  try {
    const buffer = readFileSync(filePath);
    if (buffer.byteLength > MAX_JSON_FILE_BYTES) {
      return { status: 'invalid', reason: `the file is larger than ${MAX_JSON_FILE_BYTES} bytes` };
    }
    text = buffer.toString('utf8');
  } catch (error) {
    if (errnoCode(error) === 'ENOENT') return { status: 'missing' };
    return { status: 'invalid', reason: errorMessage(error) };
  }
  // A UTF-8 byte order mark (added by some editors) is not part of the JSON.
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  try {
    return { status: 'ok', value: JSON.parse(text) as unknown };
  } catch (error) {
    return { status: 'invalid', reason: errorMessage(error) };
  }
}

export interface AtomicWriteOptions {
  /** Rename implementation (tests inject failures). Defaults to `fs.promises.rename`. */
  readonly rename?: (from: string, to: string) => Promise<void>;
  /** Delay before each rename retry. Defaults to {@link RENAME_RETRY_DELAYS_MS}. */
  readonly retryDelaysMs?: readonly number[];
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function renameWithRetry(from: string, to: string, options: AtomicWriteOptions): Promise<void> {
  const rename = options.rename ?? fsp.rename;
  const delays = options.retryDelaysMs ?? RENAME_RETRY_DELAYS_MS;
  for (let attempt = 0; ; attempt += 1) {
    try {
      await rename(from, to);
      return;
    } catch (error) {
      const delayMs = delays[attempt];
      const code = errnoCode(error);
      // Antivirus scanners, indexers and sync clients briefly lock files on Windows.
      if (delayMs === undefined || code === undefined || !RETRYABLE_RENAME_CODES.has(code)) throw error;
      await sleep(delayMs);
    }
  }
}

/**
 * Atomically replaces `filePath` with `value` as pretty-printed JSON: the folder is created when
 * missing, a temp file next to the target is written and fsynced, then renamed over the target.
 * Readers see the old or the new file, never a partial one. The temp file is removed on failure.
 */
export async function writeJsonFileAtomic(
  filePath: string,
  value: unknown,
  options: AtomicWriteOptions = {},
): Promise<void> {
  const directory = path.dirname(filePath);
  mkdirSync(directory, { recursive: true });
  const tempPath = path.join(directory, `.${path.basename(filePath)}.${randomBytes(6).toString('hex')}.tmp`);
  let handle: fsp.FileHandle | undefined;
  let tempCreated = false;
  try {
    handle = await fsp.open(tempPath, 'wx', 0o600);
    tempCreated = true;
    await handle.writeFile(`${JSON.stringify(value, null, 2)}\n`, 'utf8');
    await handle.sync();
    await handle.close();
    handle = undefined;
    await renameWithRetry(tempPath, filePath, options);
    tempCreated = false;
  } catch (error) {
    await handle?.close().catch(() => undefined);
    if (tempCreated) await fsp.rm(tempPath, { force: true }).catch(() => undefined);
    throw error;
  }
}

/** Deletes a file; a file that does not exist is not an error. */
export async function removeFile(filePath: string): Promise<void> {
  await fsp.rm(filePath, { force: true });
}
