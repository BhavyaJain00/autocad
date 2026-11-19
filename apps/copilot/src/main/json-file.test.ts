import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { errorMessage, MAX_JSON_FILE_BYTES, readJsonFile, removeFile, writeJsonFileAtomic } from './json-file';

let directory: string;

beforeEach(() => {
  directory = mkdtempSync(path.join(os.tmpdir(), 'copilot-json-'));
});

afterEach(() => {
  rmSync(directory, { recursive: true, force: true });
});

function errno(code: string): Error {
  return Object.assign(new Error(code), { code });
}

describe('readJsonFile', () => {
  it('reports a missing file', () => {
    expect(readJsonFile(path.join(directory, 'none.json'))).toEqual({ status: 'missing' });
  });

  it('parses JSON, with or without a byte order mark', () => {
    writeFileSync(path.join(directory, 'a.json'), '{"a":1}');
    writeFileSync(path.join(directory, 'b.json'), '﻿[1,2]');
    expect(readJsonFile(path.join(directory, 'a.json'))).toEqual({ status: 'ok', value: { a: 1 } });
    expect(readJsonFile(path.join(directory, 'b.json'))).toEqual({ status: 'ok', value: [1, 2] });
  });

  it('reports malformed, oversized and unreadable files as invalid', () => {
    writeFileSync(path.join(directory, 'bad.json'), '{oops');
    expect(readJsonFile(path.join(directory, 'bad.json')).status).toBe('invalid');
    writeFileSync(path.join(directory, 'big.json'), Buffer.alloc(MAX_JSON_FILE_BYTES + 1, 0x20));
    expect(readJsonFile(path.join(directory, 'big.json')).status).toBe('invalid');
    // A directory cannot be read as a file.
    expect(readJsonFile(directory).status).toBe('invalid');
  });
});

describe('writeJsonFileAtomic', () => {
  it('creates missing folders and writes pretty JSON with a final newline', async () => {
    const file = path.join(directory, 'a', 'b', 'data.json');
    await writeJsonFileAtomic(file, { x: 1 });
    expect(readFileSync(file, 'utf8')).toBe('{\n  "x": 1\n}\n');
    expect(readdirSync(path.dirname(file))).toEqual(['data.json']);
  });

  it('replaces an existing file', async () => {
    const file = path.join(directory, 'data.json');
    await writeJsonFileAtomic(file, { v: 1 });
    await writeJsonFileAtomic(file, { v: 2 });
    expect(readJsonFile(file)).toEqual({ status: 'ok', value: { v: 2 } });
  });

  it('retries a rename that fails with a transient lock error', async () => {
    const file = path.join(directory, 'data.json');
    let attempts = 0;
    const { rename } = await import('node:fs/promises');
    await writeJsonFileAtomic(
      file,
      { ok: true },
      {
        retryDelaysMs: [1, 1],
        rename: async (from, to) => {
          attempts += 1;
          if (attempts < 3) throw errno('EBUSY');
          await rename(from, to);
        },
      },
    );
    expect(attempts).toBe(3);
    expect(readJsonFile(file)).toEqual({ status: 'ok', value: { ok: true } });
  });

  it('keeps the old file and removes the temp file when the rename keeps failing', async () => {
    const file = path.join(directory, 'data.json');
    await writeJsonFileAtomic(file, { v: 1 });
    await expect(
      writeJsonFileAtomic(file, { v: 2 }, { retryDelaysMs: [1], rename: async () => Promise.reject(errno('EPERM')) }),
    ).rejects.toThrow('EPERM');
    expect(readJsonFile(file)).toEqual({ status: 'ok', value: { v: 1 } });
    expect(readdirSync(directory)).toEqual(['data.json']);
  });

  it('does not retry errors that are not transient', async () => {
    let attempts = 0;
    await expect(
      writeJsonFileAtomic(
        path.join(directory, 'data.json'),
        {},
        {
          retryDelaysMs: [1, 1, 1],
          rename: async () => {
            attempts += 1;
            throw errno('ENOSPC');
          },
        },
      ),
    ).rejects.toThrow('ENOSPC');
    expect(attempts).toBe(1);
    expect(readdirSync(directory)).toEqual([]);
  });
});

describe('removeFile', () => {
  it('deletes a file and tolerates a missing one', async () => {
    const file = path.join(directory, 'gone.json');
    writeFileSync(file, '{}');
    await removeFile(file);
    expect(existsSync(file)).toBe(false);
    await expect(removeFile(file)).resolves.toBeUndefined();
  });
});

describe('errorMessage', () => {
  it('describes errors and other thrown values', () => {
    expect(errorMessage(new Error('boom'))).toBe('boom');
    expect(errorMessage('text')).toBe('text');
    expect(errorMessage(undefined)).toBe('undefined');
  });
});
