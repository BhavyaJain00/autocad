import { describe, expect, it } from 'vitest';
import {
  BRIDGE_PROTOCOL_VERSION,
  BridgeError,
  BridgeErrorCode,
  NDJSON_MAX_LINE_LENGTH,
  NdjsonDecoder,
  UNITS,
  encodeNdjson,
  failedToolResult,
  isBridgeError,
  isRpcFailure,
  isRpcNotification,
  isRpcRequest,
  isRpcSuccess,
} from './index';
import type { RpcMessage } from './index';

describe('encodeNdjson', () => {
  it('writes one JSON line terminated by a newline', () => {
    const message: RpcMessage = { jsonrpc: '2.0', id: 1, method: 'hello', params: {} };
    expect(encodeNdjson(message)).toBe('{"jsonrpc":"2.0","id":1,"method":"hello","params":{}}\n');
  });

  it('escapes embedded line breaks so a message is always exactly one line', () => {
    const message: RpcMessage = {
      jsonrpc: '2.0',
      id: 'a',
      result: { text: 'first\nsecond\r\nthird', unicode: 'é ✓  ' },
    };
    const line = encodeNdjson(message);
    expect(line.endsWith('\n')).toBe(true);
    expect(line.slice(0, -1)).not.toMatch(/[\n\r]/);
    expect(JSON.parse(line)).toEqual(message);
  });

  it('round-trips through the decoder', () => {
    const messages: RpcMessage[] = [
      { jsonrpc: '2.0', id: 1, method: 'tools.call', params: { name: 'create_line' } },
      { jsonrpc: '2.0', method: 'document.changed', params: { documentId: 'd', revision: 4 } },
      { jsonrpc: '2.0', id: 1, result: null },
      { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } },
    ];
    const decoder = new NdjsonDecoder();
    const out = decoder.push(messages.map(encodeNdjson).join(''));
    expect(out.messages).toEqual(messages);
    expect(out.errors).toEqual([]);
  });
});

describe('NdjsonDecoder', () => {
  it('buffers a line split in the middle across chunks', () => {
    const decoder = new NdjsonDecoder();
    const line = '{"jsonrpc":"2.0","id":7,"result":{"ok":true}}\n';
    expect(decoder.push(line.slice(0, 11))).toEqual({ messages: [], errors: [] });
    expect(decoder.pendingLength).toBe(11);
    expect(decoder.push(line.slice(11, 30))).toEqual({ messages: [], errors: [] });
    expect(decoder.push(line.slice(30))).toEqual({
      messages: [{ jsonrpc: '2.0', id: 7, result: { ok: true } }],
      errors: [],
    });
    expect(decoder.pendingLength).toBe(0);
  });

  it('decodes a message delivered one character at a time', () => {
    const decoder = new NdjsonDecoder();
    const text = '{"a":[1,2,3],"b":"x\\ny"}\n{"c":null}\n';
    const messages: unknown[] = [];
    for (const char of text) messages.push(...decoder.push(char).messages);
    expect(messages).toEqual([{ a: [1, 2, 3], b: 'x\ny' }, { c: null }]);
  });

  it('returns several messages from one chunk and keeps the unfinished tail', () => {
    const decoder = new NdjsonDecoder();
    const out = decoder.push('{"n":1}\n{"n":2}\n{"n":3}\n{"n":');
    expect(out.messages).toEqual([{ n: 1 }, { n: 2 }, { n: 3 }]);
    expect(decoder.push('4}\n').messages).toEqual([{ n: 4 }]);
  });

  it('accepts CRLF line ends, also when the CR and LF arrive in different chunks', () => {
    const decoder = new NdjsonDecoder();
    expect(decoder.push('{"n":1}\r\n{"n":2}\r\n').messages).toEqual([{ n: 1 }, { n: 2 }]);
    expect(decoder.push('{"n":3}\r').messages).toEqual([]);
    expect(decoder.push('\n').messages).toEqual([{ n: 3 }]);
  });

  it('skips empty and whitespace-only lines', () => {
    const decoder = new NdjsonDecoder();
    const out = decoder.push('\n\r\n   \n{"n":1}\n\n\t\n{"n":2}\n');
    expect(out).toEqual({ messages: [{ n: 1 }, { n: 2 }], errors: [] });
  });

  it('reports an invalid line and still decodes the lines around it', () => {
    const decoder = new NdjsonDecoder();
    const out = decoder.push('{"n":1}\n{not json}\n{"n":2}\n');
    expect(out.messages).toEqual([{ n: 1 }, { n: 2 }]);
    expect(out.errors).toHaveLength(1);
    expect(out.errors[0]).toMatch(/^Invalid JSON line \(.+\): \{not json\}$/);
  });

  it('reports each invalid line, including ones split across chunks', () => {
    const decoder = new NdjsonDecoder();
    const first = decoder.push('oops\n{"ok":1}\n[1,2');
    expect(first.messages).toEqual([{ ok: 1 }]);
    expect(first.errors).toHaveLength(1);
    const second = decoder.push(',\n"still fine"\n');
    expect(second.messages).toEqual(['still fine']);
    expect(second.errors).toHaveLength(1);
    expect(second.errors[0]).toContain('[1,2,');
  });

  it('shortens the preview of a long invalid line', () => {
    const decoder = new NdjsonDecoder();
    const out = decoder.push(`${'x'.repeat(5000)}\n`);
    expect(out.errors).toHaveLength(1);
    expect(out.errors[0]?.length).toBeLessThan(250);
    expect(out.errors[0]?.endsWith('...')).toBe(true);
  });

  it('passes any JSON value through (shape checks belong to the caller)', () => {
    const decoder = new NdjsonDecoder();
    expect(decoder.push('42\n"text"\nnull\n[1]\ntrue\n').messages).toEqual([42, 'text', null, [1], true]);
  });

  it('keeps multi-byte characters intact', () => {
    const decoder = new NdjsonDecoder();
    const message = { text: 'Größe ≈ 12′ 6″ — 平面図 🏠' };
    const line = `${JSON.stringify(message)}\n`;
    const middle = Math.floor(line.length / 2);
    decoder.push(line.slice(0, middle));
    expect(decoder.push(line.slice(middle)).messages).toEqual([message]);
  });

  it('rejects a line longer than the limit that arrives in one chunk', () => {
    const decoder = new NdjsonDecoder({ maxLineLength: 32 });
    const out = decoder.push(`{"n":1}\n"${'x'.repeat(40)}"\n{"n":2}\n`);
    expect(out.messages).toEqual([{ n: 1 }, { n: 2 }]);
    expect(out.errors).toEqual(['Line exceeds the maximum length of 32 characters and was discarded']);
    expect(decoder.pendingLength).toBe(0);
  });

  it('rejects an oversize line as soon as the buffer overflows and drops the rest of that line', () => {
    const decoder = new NdjsonDecoder({ maxLineLength: 32 });
    expect(decoder.push('"aaaaaaaaaaaaaaaaaaaa').errors).toEqual([]);
    const overflow = decoder.push('bbbbbbbbbbbbbbbbbbbb');
    expect(overflow.errors).toEqual(['Line exceeds the maximum length of 32 characters and was discarded']);
    expect(decoder.pendingLength).toBe(0);
    // The remainder of the oversize line is discarded silently, even if it looks like JSON...
    expect(decoder.push('cccc')).toEqual({ messages: [], errors: [] });
    expect(decoder.push('{"n":0}')).toEqual({ messages: [], errors: [] });
    // ...and decoding resumes with the next line.
    expect(decoder.push('"\n{"n":1}\n')).toEqual({ messages: [{ n: 1 }], errors: [] });
  });

  it('accepts a line of exactly the maximum length', () => {
    const decoder = new NdjsonDecoder({ maxLineLength: 10 });
    expect(decoder.push('"12345678"\n')).toEqual({ messages: ['12345678'], errors: [] });
    expect(decoder.push('"123456789"\n').errors).toHaveLength(1);
    expect(decoder.push('"12345').errors).toEqual([]);
    expect(decoder.push('678"\n')).toEqual({ messages: ['12345678'], errors: [] });
  });

  it('enforces the 32 MiB default limit and recovers', () => {
    expect(NDJSON_MAX_LINE_LENGTH).toBe(32 * 1024 * 1024);
    const decoder = new NdjsonDecoder();
    const eightMiB = 'x'.repeat(8 * 1024 * 1024);
    for (let index = 0; index < 4; index += 1) {
      expect(decoder.push(eightMiB).errors).toEqual([]);
    }
    expect(decoder.pendingLength).toBe(NDJSON_MAX_LINE_LENGTH);
    const overflow = decoder.push('x');
    expect(overflow.errors).toHaveLength(1);
    expect(overflow.errors[0]).toContain(String(NDJSON_MAX_LINE_LENGTH));
    expect(decoder.pendingLength).toBe(0);
    expect(decoder.push(`${eightMiB}\n{"after":true}\n`)).toEqual({ messages: [{ after: true }], errors: [] });
  });

  it('decodes a large valid line delivered in many chunks', () => {
    const decoder = new NdjsonDecoder();
    const payload = { data: 'y'.repeat(2 * 1024 * 1024) };
    const line = `${JSON.stringify(payload)}\n`;
    const messages: unknown[] = [];
    for (let offset = 0; offset < line.length; offset += 65_536) {
      const out = decoder.push(line.slice(offset, offset + 65_536));
      expect(out.errors).toEqual([]);
      messages.push(...out.messages);
    }
    expect(messages).toHaveLength(1);
    expect(messages[0]).toEqual(payload);
  });

  it('reset() drops a partial line', () => {
    const decoder = new NdjsonDecoder();
    decoder.push('{"half":');
    decoder.reset();
    expect(decoder.pendingLength).toBe(0);
    expect(decoder.push('{"n":1}\n')).toEqual({ messages: [{ n: 1 }], errors: [] });
  });

  it('falls back to the default limit for a nonsensical option', () => {
    for (const maxLineLength of [0, -5, Number.NaN, Number.POSITIVE_INFINITY]) {
      const decoder = new NdjsonDecoder({ maxLineLength });
      expect(decoder.push(`"${'z'.repeat(1000)}"\n`).errors).toEqual([]);
    }
  });

  it('handles an empty chunk', () => {
    const decoder = new NdjsonDecoder();
    expect(decoder.push('')).toEqual({ messages: [], errors: [] });
  });
});

describe('JSON-RPC type guards', () => {
  const request = { jsonrpc: '2.0', id: 1, method: 'hello' };
  const notification = { jsonrpc: '2.0', method: 'selection.changed', params: { ids: [] } };
  const success = { jsonrpc: '2.0', id: 'x', result: null };
  const failure = { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } };

  it('classifies each message kind exclusively', () => {
    const guards = [isRpcRequest, isRpcNotification, isRpcSuccess, isRpcFailure];
    const samples = [request, notification, success, failure];
    samples.forEach((sample, sampleIndex) => {
      guards.forEach((guard, guardIndex) => {
        expect(guard(sample)).toBe(sampleIndex === guardIndex);
      });
    });
  });

  it('rejects malformed envelopes', () => {
    const malformed: unknown[] = [
      null,
      undefined,
      42,
      'hello',
      [],
      {},
      { id: 1, method: 'hello' },
      { jsonrpc: '1.0', id: 1, method: 'hello' },
      { jsonrpc: '2.0', id: {}, method: 'hello' },
      { jsonrpc: '2.0', id: 1, method: 7 },
      { jsonrpc: '2.0', id: 1 },
      { jsonrpc: '2.0', id: 1, error: 'bad' },
      { jsonrpc: '2.0', id: 1, error: { code: 'x', message: 'm' } },
      { jsonrpc: '2.0', id: Number.NaN, method: 'hello' },
    ];
    for (const value of malformed) {
      expect(isRpcRequest(value)).toBe(false);
      expect(isRpcNotification(value)).toBe(false);
      expect(isRpcSuccess(value)).toBe(false);
      expect(isRpcFailure(value)).toBe(false);
    }
  });

  it('treats a null id as a failure-only id', () => {
    expect(isRpcRequest({ jsonrpc: '2.0', id: null, method: 'hello' })).toBe(false);
    expect(isRpcNotification({ jsonrpc: '2.0', id: null, method: 'hello' })).toBe(false);
    expect(isRpcSuccess({ jsonrpc: '2.0', id: null, result: 1 })).toBe(false);
  });
});

describe('core values', () => {
  it('exposes the protocol constants', () => {
    expect(BRIDGE_PROTOCOL_VERSION).toBe(1);
    expect(UNITS).toEqual(['mm', 'cm', 'm', 'in', 'ft']);
    expect(BridgeErrorCode).toMatchObject({
      Unauthorized: -32001,
      UnknownTool: -32002,
      InvalidArguments: -32003,
      StaleRevision: -32004,
      NoActiveDocument: -32005,
      Busy: -32006,
      CadOperationFailed: -32007,
      NotSupported: -32008,
      ParseError: -32700,
      InvalidRequest: -32600,
      MethodNotFound: -32601,
      InvalidParams: -32602,
      InternalError: -32603,
    });
  });

  it('exports BridgeErrorCode as a value and as a type', () => {
    const code: BridgeErrorCode = BridgeErrorCode.MethodNotFound;
    expect(code).toBe(-32601);
    expect(new Set(Object.values(BridgeErrorCode)).size).toBe(Object.keys(BridgeErrorCode).length);
  });

  it('BridgeError carries a code, a message and optional data', () => {
    const error = new BridgeError(BridgeErrorCode.Busy, 'busy', { retryInMs: 500 });
    expect(error).toBeInstanceOf(Error);
    expect(error.name).toBe('BridgeError');
    expect(error.code).toBe(-32006);
    expect(error.message).toBe('busy');
    expect(error.data).toEqual({ retryInMs: 500 });
    expect(new BridgeError(1, 'm').data).toBeUndefined();
    expect(isBridgeError(error)).toBe(true);
    expect(isBridgeError(new Error('plain'))).toBe(false);
    expect(isBridgeError({ code: 1, message: 'm', name: 'BridgeError' })).toBe(false);
    const foreign = Object.assign(new Error('other realm'), { name: 'BridgeError', code: -32001 });
    expect(isBridgeError(foreign)).toBe(true);
  });

  it('failedToolResult builds a failure envelope', () => {
    expect(failedToolResult('No drawing is open', 4)).toEqual({
      success: false,
      createdIds: [],
      modifiedIds: [],
      deletedIds: [],
      warnings: [],
      errors: ['No drawing is open'],
      revision: 4,
    });
    const errors = ['a', 'b'];
    const result = failedToolResult(errors, 0);
    expect(result.errors).toEqual(['a', 'b']);
    expect(result.errors).not.toBe(errors);
  });
});
