/**
 * The Anthropic API key: encrypted with the operating system's credential protection (Electron
 * `safeStorage`: DPAPI on Windows, Keychain on macOS, libsecret on Linux) and kept as base64 in
 * `<userData>/credentials.json`. It is decrypted only in the main process and never sent to the
 * renderer. The encryption backend is injected, so this module has no Electron imports.
 */
import { errorMessage, readJsonFile, removeFile, writeJsonFileAtomic } from './json-file';

/** The subset of Electron's `safeStorage` used here. Only valid after the app is ready. */
export interface SecretProtector {
  isEncryptionAvailable(): boolean;
  encryptString(plainText: string): Buffer;
  decryptString(encrypted: Buffer): string;
}

export type CredentialResult = { ok: true } | { ok: false; message: string };

/** Version of the credentials file layout. */
export const CREDENTIALS_FILE_VERSION = 1;

/** Longest API key accepted. */
export const API_KEY_MAX_LENGTH = 512;
/** Shortest API key accepted. */
export const API_KEY_MIN_LENGTH = 16;

export const ENCRYPTION_UNAVAILABLE_MESSAGE =
  'The API key cannot be stored on this computer because the operating system does not offer ' +
  'secure credential storage here. Set the ANTHROPIC_API_KEY environment variable instead and ' +
  'restart CAD Copilot.';

export type ApiKeyCheck = { ok: true; key: string } | { ok: false; message: string };

/**
 * Trims a typed or pasted API key and checks that it can be one: printable ASCII without spaces,
 * of a plausible length. Whether the key actually works is checked against the API afterwards.
 */
export function checkApiKeyShape(value: unknown): ApiKeyCheck {
  if (typeof value !== 'string') return { ok: false, message: 'The API key must be text.' };
  const key = value.trim();
  if (key.length === 0) return { ok: false, message: 'Enter an API key.' };
  if (key.length > API_KEY_MAX_LENGTH) return { ok: false, message: 'That API key is too long.' };
  if (!/^[\x21-\x7e]+$/.test(key)) {
    return { ok: false, message: 'An API key cannot contain spaces, line breaks or special characters.' };
  }
  if (key.length < API_KEY_MIN_LENGTH) {
    return { ok: false, message: 'That API key is too short. Paste the whole key (it starts with "sk-ant-").' };
  }
  return { ok: true, key };
}

export interface CredentialStoreOptions {
  /** `<userData>/credentials.json`. */
  readonly filePath: string;
  readonly protector: SecretProtector;
  /** The process environment's API key (ANTHROPIC_API_KEY), read when nothing is stored. */
  readonly environmentKey: () => string | undefined;
  readonly log?: (message: string) => void;
}

/** Where the API key in use comes from. */
export type ApiKeySource = 'stored' | 'environment' | 'none';

/**
 * Holds the API key for the main process. Call `load()` after the app is ready (the operating
 * system's encryption is not available earlier).
 */
export class CredentialStore {
  readonly #options: CredentialStoreOptions;
  readonly #log: (message: string) => void;
  #storedKey: string | null = null;

  constructor(options: CredentialStoreOptions) {
    this.#options = options;
    this.#log = options.log ?? (() => undefined);
  }

  /** Reads and decrypts the stored key. A missing, damaged or undecryptable file means no key. */
  load(): void {
    this.#storedKey = null;
    const read = readJsonFile(this.#options.filePath);
    if (read.status === 'missing') return;
    if (read.status === 'invalid') {
      this.#log(`The credentials file could not be read (${read.reason}).`);
      return;
    }
    const encoded = (read.value as { apiKey?: unknown } | null)?.apiKey;
    if (typeof encoded !== 'string' || encoded.length === 0) return;
    const { protector } = this.#options;
    try {
      if (!protector.isEncryptionAvailable()) {
        this.#log('A stored API key exists but secure storage is not available; it is ignored.');
        return;
      }
      const key = protector.decryptString(Buffer.from(encoded, 'base64'));
      const checked = checkApiKeyShape(key);
      if (checked.ok) this.#storedKey = checked.key;
      else this.#log('The stored API key is not usable; it is ignored.');
    } catch (error) {
      // Encrypted by another user account or machine, or the file was edited.
      this.#log(`The stored API key could not be decrypted (${errorMessage(error)}); it is ignored.`);
    }
  }

  /** The key to use: the stored one, otherwise the environment's, otherwise null. */
  apiKey(): string | null {
    if (this.#storedKey !== null) return this.#storedKey;
    const fromEnvironment = this.#options.environmentKey()?.trim();
    return fromEnvironment !== undefined && fromEnvironment.length > 0 ? fromEnvironment : null;
  }

  source(): ApiKeySource {
    if (this.#storedKey !== null) return 'stored';
    return this.apiKey() !== null ? 'environment' : 'none';
  }

  /** True when a key is available (stored or from the environment). */
  hasApiKey(): boolean {
    return this.apiKey() !== null;
  }

  /** Why a key cannot be stored on this system, or null when it can. */
  storageProblem(): string | null {
    try {
      return this.#options.protector.isEncryptionAvailable() ? null : ENCRYPTION_UNAVAILABLE_MESSAGE;
    } catch (error) {
      return `${ENCRYPTION_UNAVAILABLE_MESSAGE} (${errorMessage(error)})`;
    }
  }

  /** Encrypts and stores a key (already checked and verified by the caller). */
  async store(key: string): Promise<CredentialResult> {
    const problem = this.storageProblem();
    if (problem !== null) return { ok: false, message: problem };
    try {
      const encrypted = this.#options.protector.encryptString(key);
      await writeJsonFileAtomic(this.#options.filePath, {
        version: CREDENTIALS_FILE_VERSION,
        apiKey: encrypted.toString('base64'),
      });
    } catch (error) {
      return { ok: false, message: `The API key could not be stored: ${errorMessage(error)}` };
    }
    this.#storedKey = key;
    return { ok: true };
  }

  /** Removes the stored key (the environment's key, if any, is used from then on). */
  async clear(): Promise<CredentialResult> {
    try {
      await removeFile(this.#options.filePath);
    } catch (error) {
      return { ok: false, message: `The stored API key could not be removed: ${errorMessage(error)}` };
    }
    this.#storedKey = null;
    return { ok: true };
  }
}
