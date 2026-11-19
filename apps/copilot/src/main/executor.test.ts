import { describe, expect, it } from 'vitest';
import type { DocumentInfo, HelloResult, ToolCallParams, ToolResult } from '@cadstudio/cad-protocol';
import { cadDisplayName, createConnectionExecutor, type ToolCallConnection } from './executor';

const DOCUMENT: DocumentInfo = { id: 'doc-1', name: 'Plan.cadstudio', path: null, unit: 'mm', revision: 7, entityCount: 3 };

const RESULT: ToolResult = {
  success: true,
  data: { id: 'e1' },
  createdIds: ['e1'],
  modifiedIds: [],
  deletedIds: [],
  warnings: [],
  errors: [],
  revision: 8,
};

function hello(overrides: Partial<HelloResult> = {}): HelloResult {
  return {
    cad: 'cadstudio',
    cadVersion: '0.1.0',
    connectorVersion: '0.1.0',
    protocolVersion: 1,
    document: DOCUMENT,
    supportedTools: ['get_drawing_info', 'create_line'],
    ...overrides,
  };
}

interface RecordedCall {
  params: ToolCallParams;
  opts: { signal?: AbortSignal; timeoutMs?: number } | undefined;
}

class FakeConnection implements ToolCallConnection {
  advert = { displayName: 'CAD Studio', cad: 'cadstudio' as const, cadVersion: '0.1.0' };
  readonly calls: RecordedCall[] = [];
  next: () => Promise<ToolResult> = () => Promise.resolve(RESULT);
  callTool(params: ToolCallParams, opts?: { signal?: AbortSignal; timeoutMs?: number }): Promise<ToolResult> {
    this.calls.push({ params, opts });
    return this.next();
  }
}

describe('cadDisplayName', () => {
  it('uses the advert display name', () => {
    expect(cadDisplayName({ displayName: ' AutoCAD 2026 ', cad: 'autocad', cadVersion: '25.1' })).toBe('AutoCAD 2026');
  });

  it('derives a name from the CAD kind when the advert has none', () => {
    expect(cadDisplayName({ displayName: '', cad: 'autocad', cadVersion: '2026' })).toBe('AutoCAD 2026');
    expect(cadDisplayName({ displayName: '  ', cad: 'cadstudio', cadVersion: '' })).toBe('CAD Studio');
  });
});

describe('createConnectionExecutor', () => {
  it('reports the CAD name, tools and drawing from the latest hello', () => {
    const connection = new FakeConnection();
    let latest = hello();
    const executor = createConnectionExecutor(connection, { latestHello: () => latest });

    expect(executor.cadName()).toBe('CAD Studio');
    expect(executor.supportedTools()).toEqual(['get_drawing_info', 'create_line']);
    expect(executor.currentDocument()).toEqual(DOCUMENT);

    // A refreshed hello is visible without recreating the executor.
    latest = hello({ document: { ...DOCUMENT, revision: 9, entityCount: 4 }, supportedTools: ['get_drawing_info'] });
    expect(executor.supportedTools()).toEqual(['get_drawing_info']);
    expect(executor.currentDocument()).toMatchObject({ revision: 9, entityCount: 4 });

    latest = hello({ document: null });
    expect(executor.currentDocument()).toBeNull();
  });

  it('returns copies so callers cannot change the connector state', () => {
    const latest = hello();
    const executor = createConnectionExecutor(new FakeConnection(), { latestHello: () => latest });
    (executor.supportedTools() as string[]).push('delete_entities');
    const document = executor.currentDocument();
    if (document !== null) document.revision = 999;
    expect(latest.supportedTools).toEqual(['get_drawing_info', 'create_line']);
    expect(latest.document?.revision).toBe(7);
  });

  it('sends a tool call with source "ai", the instruction, the revision and the abort signal', async () => {
    const connection = new FakeConnection();
    const executor = createConnectionExecutor(connection, { latestHello: hello, newRequestId: () => 'req-1' });
    const controller = new AbortController();
    const args = { start: { x: 0, y: 0 }, end: { x: 1000, y: 0 } };

    const result = await executor.execute('create_line', args, {
      operationId: 'op-42',
      instruction: 'draw a 1 m line',
      signal: controller.signal,
      expectedRevision: 7,
    });

    expect(result).toBe(RESULT);
    expect(connection.calls).toHaveLength(1);
    expect(connection.calls[0]?.params).toEqual({
      requestId: 'req-1',
      operationId: 'op-42',
      name: 'create_line',
      arguments: args,
      expectedRevision: 7,
      source: 'ai',
      instruction: 'draw a 1 m line',
    });
    expect(connection.calls[0]?.opts?.signal).toBe(controller.signal);
  });

  it('omits expectedRevision when the session gives none', async () => {
    const connection = new FakeConnection();
    const executor = createConnectionExecutor(connection, { latestHello: hello });
    await executor.execute('get_drawing_info', {}, { operationId: 'op-1', instruction: 'inspect', signal: new AbortController().signal });
    const params = connection.calls[0]?.params;
    expect(params).toBeDefined();
    expect(params !== undefined && 'expectedRevision' in params).toBe(false);
    expect(params?.source).toBe('ai');
  });

  it('gives every call its own request id', async () => {
    const connection = new FakeConnection();
    const executor = createConnectionExecutor(connection, { latestHello: hello });
    const opts = { operationId: 'op-1', instruction: 'x', signal: new AbortController().signal };
    await executor.execute('get_drawing_info', {}, opts);
    await executor.execute('get_drawing_info', {}, opts);
    const [first, second] = connection.calls.map((call) => call.params.requestId);
    expect(first).toMatch(/^[0-9a-f-]{36}$/);
    expect(second).toMatch(/^[0-9a-f-]{36}$/);
    expect(first).not.toBe(second);
  });

  it('returns failed tool results as results and lets transport errors reject', async () => {
    const connection = new FakeConnection();
    const executor = createConnectionExecutor(connection, { latestHello: hello });
    const opts = { operationId: 'op-1', instruction: 'x', signal: new AbortController().signal };

    const failed: ToolResult = { ...RESULT, success: false, createdIds: [], errors: ['The drawing changed'], revision: 9 };
    connection.next = () => Promise.resolve(failed);
    expect(await executor.execute('create_line', {}, opts)).toBe(failed);

    connection.next = () => Promise.reject(new Error('The connection to CAD Studio is closed'));
    await expect(executor.execute('create_line', {}, opts)).rejects.toThrow('connection to CAD Studio is closed');
  });
});
