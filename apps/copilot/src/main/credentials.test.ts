import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  API_KEY_MAX_LENGTH,
  checkApiKeyShape,
  CREDENTIALS_FILE_VERSION,
  CredentialStore,
  ENCRYPTION_UNAVAILABLE_MESSAGE,
  type SecretProtector,
} from './credentials';

const KEY = 'sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789';
const OTHER_KEY = 'sk-ant-api03-zyxwvutsrqponmlkjihgfedcba9876543210';

/** A reversible stand-in for the OS encryption: XOR with a constant, prefixed with a marker. */
class FakeProtector implements SecretProtector {
  available = true;
  failDecrypt = false;
  isEncryptionAvailable(): boolean {
    return this.available;
  }
  encryptString(plainText: string): Buffer {
    if (!this.available) throw new Error('encryption is not available');
    const data = Buffer.from(plainText, 'utf8').map((byte) => byte ^ 0x5a);
    return Buffer.concat([Buffer.from('v1:'), data]);
  }
  decryptString(encrypted: Buffer): string {
    if (this.failDecrypt || encrypted.subarray(0, 3).toString() !== 'v1:') throw new Error('cannot decrypt');
    return Buffer.from(encrypted.subarray(3).map((byte) => byte ^ 0x5a)).toString('utf8');
  }
}

describe('checkApiKeyShape', () => {
  it('trims and accepts a plausible key', () => {
    expect(checkApiKeyShape(`  ${KEY}\n`)).toEqual({ ok: true, key: KEY });
  });

  it('rejects values that cannot be a key, with a reason', () => {
    const invalid: unknown[] = [
      undefined,
      null,
      42,
      {},
      '',
      '    ',
      'short',
      'sk-ant with spaces inside the key value',
      'sk-ant-\nline-break-in-the-middle-of-key',
      'sk-ant-ключ-not-ascii-0123456789',
      'x'.repeat(API_KEY_MAX_LENGTH + 1),
    ];
    for (const value of invalid) {
      const result = checkApiKeyShape(value);
      expect(result.ok, String(value).slice(0, 30)).toBe(false);
      if (!result.ok) expect(result.message.length).toBeGreaterThan(0);
    }
  });
});

describe('CredentialStore', () => {
  let directory: string;
  let filePath: string;
  let protector: FakeProtector;
  let environmentKey: string | undefined;
  let logs: string[];

  const createStore = (): CredentialStore =>
    new CredentialStore({
      filePath,
      protector,
      environmentKey: () => environmentKey,
      log: (message) => logs.push(message),
    });

  beforeEach(() => {
    directory = mkdtempSync(path.join(os.tmpdir(), 'copilot-credentials-'));
    filePath = path.join(directory, 'credentials.json');
    protector = new FakeProtector();
    environmentKey = undefined;
    logs = [];
  });

  afterEach(() => {
    rmSync(directory, { recursive: true, force: true });
  });

  it('has no key when nothing is stored and the environment has none', () => {
    const store = createStore();
    store.load();
    expect(store.apiKey()).toBeNull();
    expect(store.hasApiKey()).toBe(false);
    expect(store.source()).toBe('none');
  });

  it('stores the key encrypted and reads it back in a new store', async () => {
    const store = createStore();
    store.load();
    expect(await store.store(KEY)).toEqual({ ok: true });
    expect(store.apiKey()).toBe(KEY);
    expect(store.source()).toBe('stored');

    const text = readFileSync(filePath, 'utf8');
    expect(text).not.toContain(KEY);
    const onDisk = JSON.parse(text) as { version: number; apiKey: string };
    expect(onDisk.version).toBe(CREDENTIALS_FILE_VERSION);
    expect(Buffer.from(onDisk.apiKey, 'base64').subarray(0, 3).toString()).toBe('v1:');

    const reopened = createStore();
    reopened.load();
    expect(reopened.apiKey()).toBe(KEY);
  });

  it('uses the environment key only when nothing is stored', async () => {
    environmentKey = `  ${OTHER_KEY}  `;
    const store = createStore();
    store.load();
    expect(store.apiKey()).toBe(OTHER_KEY);
    expect(store.source()).toBe('environment');
    expect(store.hasApiKey()).toBe(true);

    await store.store(KEY);
    expect(store.apiKey()).toBe(KEY);
    expect(store.source()).toBe('stored');

    expect(await store.clear()).toEqual({ ok: true });
    expect(existsSync(filePath)).toBe(false);
    expect(store.apiKey()).toBe(OTHER_KEY);
    expect(store.source()).toBe('environment');
  });

  it('treats a blank environment key as no key', () => {
    environmentKey = '   ';
    const store = createStore();
    store.load();
    expect(store.hasApiKey()).toBe(false);
  });

  it('refuses to store when encryption is not available and says why', async () => {
    protector.available = false;
    const store = createStore();
    store.load();
    expect(store.storageProblem()).toBe(ENCRYPTION_UNAVAILABLE_MESSAGE);
    expect(await store.store(KEY)).toEqual({ ok: false, message: ENCRYPTION_UNAVAILABLE_MESSAGE });
    expect(existsSync(filePath)).toBe(false);
    expect(store.hasApiKey()).toBe(false);
  });

  it('reports no storage problem when encryption is available', () => {
    expect(createStore().storageProblem()).toBeNull();
  });

  it('ignores a stored key that cannot be decrypted', async () => {
    await createStore().store(KEY);
    protector.failDecrypt = true;
    const store = createStore();
    store.load();
    expect(store.apiKey()).toBeNull();
    expect(logs.some((line) => line.includes('could not be decrypted'))).toBe(true);
  });

  it('ignores a stored key while encryption is unavailable', async () => {
    await createStore().store(KEY);
    protector.available = false;
    const store = createStore();
    store.load();
    expect(store.apiKey()).toBeNull();
  });

  it('ignores damaged or unexpected credentials files', () => {
    for (const content of ['{ not json', '[]', '{}', '{"apiKey": 5}', '{"apiKey": ""}', 'null']) {
      writeFileSync(filePath, content);
      const store = createStore();
      store.load();
      expect(store.apiKey(), content).toBeNull();
    }
  });

  it('ignores a stored value that decrypts to something that is not a key', () => {
    writeFileSync(filePath, JSON.stringify({ version: 1, apiKey: protector.encryptString('no').toString('base64') }));
    const store = createStore();
    store.load();
    expect(store.apiKey()).toBeNull();
  });

  it('clearing when nothing is stored succeeds', async () => {
    const store = createStore();
    store.load();
    expect(await store.clear()).toEqual({ ok: true });
  });

  it('replaces a stored key', async () => {
    const store = createStore();
    await store.store(KEY);
    await store.store(OTHER_KEY);
    const reopened = createStore();
    reopened.load();
    expect(reopened.apiKey()).toBe(OTHER_KEY);
  });
});
