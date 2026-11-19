// JSON-RPC 2.0 message shapes and newline-delimited JSON framing. Pure and browser-safe.

export interface RpcRequest {
  jsonrpc: '2.0';
  id: number | string;
  method: string;
  params?: unknown;
}
export interface RpcNotification {
  jsonrpc: '2.0';
  method: string;
  params?: unknown;
}
export interface RpcSuccess {
  jsonrpc: '2.0';
  id: number | string;
  result: unknown;
}
export interface RpcFailure {
  jsonrpc: '2.0';
  id: number | string | null;
  error: { code: number; message: string; data?: unknown };
}
export type RpcMessage = RpcRequest | RpcNotification | RpcSuccess | RpcFailure;

/** Longest accepted NDJSON line, in characters (32 MiB). Longer lines are rejected, not buffered. */
export const NDJSON_MAX_LINE_LENGTH = 32 * 1024 * 1024;

const PREVIEW_LENGTH = 60;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isRpcId(value: unknown): value is number | string {
  return typeof value === 'string' || (typeof value === 'number' && Number.isFinite(value));
}

/** A request: JSON-RPC 2.0 envelope with a method and a string/number id. */
export function isRpcRequest(value: unknown): value is RpcRequest {
  return isRecord(value) && value.jsonrpc === '2.0' && typeof value.method === 'string' && isRpcId(value.id);
}

/** A notification: JSON-RPC 2.0 envelope with a method and no id. */
export function isRpcNotification(value: unknown): value is RpcNotification {
  return (
    isRecord(value) && value.jsonrpc === '2.0' && typeof value.method === 'string' && value.id === undefined
  );
}

/** A successful response: has an id and a `result` member, and no method. */
export function isRpcSuccess(value: unknown): value is RpcSuccess {
  return (
    isRecord(value) &&
    value.jsonrpc === '2.0' &&
    value.method === undefined &&
    isRpcId(value.id) &&
    'result' in value &&
    !('error' in value)
  );
}

/** An error response: `error` with a numeric code and a message; the id may be null. */
export function isRpcFailure(value: unknown): value is RpcFailure {
  if (!isRecord(value) || value.jsonrpc !== '2.0' || value.method !== undefined) return false;
  if (!(value.id === null || isRpcId(value.id))) return false;
  const error = value.error;
  return isRecord(error) && typeof error.code === 'number' && typeof error.message === 'string';
}

/**
 * Serialises one message as a single NDJSON line: `JSON.stringify(message)` plus `\n`. Newlines
 * inside strings are escaped by JSON, so the output never contains a raw line break before the end.
 */
export function encodeNdjson(message: RpcMessage): string {
  return `${JSON.stringify(message)}\n`;
}

/**
 * Incremental NDJSON decoder: push text chunks, receive the complete parsed lines.
 *
 * - A line may be split across any number of chunks; several lines may arrive in one chunk.
 * - `\r\n` line ends are accepted; empty and whitespace-only lines are skipped.
 * - A line that is not valid JSON is reported in `errors`; later lines are still decoded.
 * - A line longer than the limit (32 MiB by default) is reported once in `errors` and discarded
 *   up to its terminating newline, so a hostile or broken peer cannot exhaust memory.
 *
 * Feed it decoded text (for sockets use `socket.setEncoding('utf8')`, which keeps multi-byte
 * characters intact across chunk boundaries).
 */
export class NdjsonDecoder {
  private readonly maxLineLength: number;
  private parts: string[] = [];
  private bufferedLength = 0;
  private discarding = false;

  constructor(options: { maxLineLength?: number } = {}) {
    const limit = options.maxLineLength;
    this.maxLineLength =
      limit !== undefined && Number.isFinite(limit) && limit > 0 ? Math.floor(limit) : NDJSON_MAX_LINE_LENGTH;
  }

  /** Number of characters of an unfinished line currently buffered. */
  get pendingLength(): number {
    return this.bufferedLength;
  }

  /** Drops any partial line (use when the underlying stream is replaced). */
  reset(): void {
    this.parts = [];
    this.bufferedLength = 0;
    this.discarding = false;
  }

  push(chunk: string): { messages: unknown[]; errors: string[] } {
    const messages: unknown[] = [];
    const errors: string[] = [];
    let start = 0;

    for (;;) {
      const newline = chunk.indexOf('\n', start);
      if (newline === -1) break;
      const piece = chunk.slice(start, newline);
      start = newline + 1;

      if (this.discarding) {
        // End of an oversize line that was already reported.
        this.discarding = false;
        continue;
      }
      if (this.bufferedLength + piece.length > this.maxLineLength) {
        this.parts = [];
        this.bufferedLength = 0;
        errors.push(this.oversizeMessage());
        continue;
      }
      let line = piece;
      if (this.bufferedLength > 0) {
        this.parts.push(piece);
        line = this.parts.join('');
        this.parts = [];
        this.bufferedLength = 0;
      }
      this.decodeLine(line, messages, errors);
    }

    if (start < chunk.length && !this.discarding) {
      const rest = start === 0 ? chunk : chunk.slice(start);
      if (this.bufferedLength + rest.length > this.maxLineLength) {
        this.parts = [];
        this.bufferedLength = 0;
        this.discarding = true;
        errors.push(this.oversizeMessage());
      } else {
        this.parts.push(rest);
        this.bufferedLength += rest.length;
      }
    }

    return { messages, errors };
  }

  private oversizeMessage(): string {
    return `Line exceeds the maximum length of ${this.maxLineLength} characters and was discarded`;
  }

  private decodeLine(rawLine: string, messages: unknown[], errors: string[]): void {
    const line = rawLine.endsWith('\r') ? rawLine.slice(0, -1) : rawLine;
    if (line.trim().length === 0) return;
    try {
      messages.push(JSON.parse(line) as unknown);
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      const preview = line.length > PREVIEW_LENGTH ? `${line.slice(0, PREVIEW_LENGTH)}...` : line;
      errors.push(`Invalid JSON line (${reason}): ${preview}`);
    }
  }
}
