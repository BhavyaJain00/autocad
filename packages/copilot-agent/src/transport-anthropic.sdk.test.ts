// The Claude transport against the REAL SDK client, with the client's `fetch` replaced by a local
// function that answers with canned responses. This checks what the SDK actually puts on the wire
// for our request and that real server-sent events come back as deltas and a final message.
// Nothing here opens a network connection.
import { describe, it, expect } from 'vitest';
import { Anthropic } from '@anthropic-ai/sdk';
import { SYSTEM_PROMPT } from './prompt';
import { CopilotSession } from './session';
import { FAKE_SOURCE, fakeExecutor, historyProblems } from './test-support';
import { buildModelTools } from './tools';
import { createAnthropicTransport, verifyApiKey } from './transport-anthropic';
import type { ModelId, ModelRequest, SessionEvent } from './types';

interface Recorded {
  url: string;
  method: string;
  headers: Headers;
  body: Record<string, unknown> | null;
}

type Responder = (request: Recorded, signal: AbortSignal | null) => Response | Promise<Response>;

/** A real SDK client whose HTTP layer is a local function. */
function clientWith(responder: Responder): { client: Anthropic; requests: Recorded[] } {
  const requests: Recorded[] = [];
  const fetchStub = async (input: unknown, init?: { method?: string; headers?: unknown; body?: unknown; signal?: AbortSignal | null }): Promise<Response> => {
    const recorded: Recorded = {
      url: String(input),
      method: init?.method ?? 'GET',
      headers: new Headers(init?.headers as ConstructorParameters<typeof Headers>[0]),
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : null,
    };
    requests.push(recorded);
    return responder(recorded, init?.signal ?? null);
  };
  const client = new Anthropic({ apiKey: 'sk-ant-test-key', maxRetries: 0, fetch: fetchStub as never });
  return { client, requests };
}

function sse(events: Record<string, unknown>[]): string {
  return events.map((event) => `event: ${String(event.type)}\ndata: ${JSON.stringify(event)}\n\n`).join('');
}

function eventStream(text: string): Response {
  return new Response(text, { status: 200, headers: { 'content-type': 'text/event-stream', 'request-id': 'req_test' } });
}

const start = (model: string): Record<string, unknown> => ({
  type: 'message_start',
  message: {
    id: 'msg_test',
    type: 'message',
    role: 'assistant',
    model,
    content: [],
    stop_reason: null,
    stop_sequence: null,
    usage: { input_tokens: 120, output_tokens: 1, cache_read_input_tokens: 4000, cache_creation_input_tokens: 0 },
  },
});

const finish = (stopReason: string, outputTokens: number): Record<string, unknown>[] => [
  { type: 'message_delta', delta: { stop_reason: stopReason, stop_sequence: null }, usage: { output_tokens: outputTokens } },
  { type: 'message_stop' },
];

function toolTurn(model: string): string {
  return sse([
    start(model),
    { type: 'content_block_start', index: 0, content_block: { type: 'thinking', thinking: '', signature: '' } },
    { type: 'content_block_delta', index: 0, delta: { type: 'signature_delta', signature: 'sig-abc' } },
    { type: 'content_block_stop', index: 0 },
    { type: 'content_block_start', index: 1, content_block: { type: 'text', text: '' } },
    { type: 'content_block_delta', index: 1, delta: { type: 'text_delta', text: 'Drawing ' } },
    { type: 'content_block_delta', index: 1, delta: { type: 'text_delta', text: 'the line.' } },
    { type: 'content_block_stop', index: 1 },
    { type: 'content_block_start', index: 2, content_block: { type: 'tool_use', id: 'toolu_01', name: 'draw_line', input: {} } },
    { type: 'content_block_delta', index: 2, delta: { type: 'input_json_delta', partial_json: '{"x1":0,"y1":0,' } },
    { type: 'content_block_delta', index: 2, delta: { type: 'input_json_delta', partial_json: '"x2":250,"y2":0}' } },
    { type: 'content_block_stop', index: 2 },
    ...finish('tool_use', 40),
  ]);
}

function textTurn(model: string, text: string): string {
  return sse([
    start(model),
    { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } },
    { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text } },
    { type: 'content_block_stop', index: 0 },
    ...finish('end_turn', 8),
  ]);
}

function request(model: ModelId): ModelRequest {
  return {
    model,
    effort: 'high',
    system: SYSTEM_PROMPT,
    tools: buildModelTools(FAKE_SOURCE, ['get_info', 'draw_line']),
    messages: [{ role: 'user', content: [{ type: 'text', text: 'Draw a line' }] }],
  };
}

describe('Claude transport on the real SDK client (local fetch, no network)', () => {
  it('puts the request on the wire the way the API expects it', async () => {
    const { client, requests } = clientWith(() => eventStream(textTurn('claude-opus-5-5', 'Hello.')));
    const transport = createAnthropicTransport({ client });
    const deltas: string[] = [];
    const response = await transport.run(request('claude-opus-5-5'), {
      signal: new AbortController().signal,
      onTextDelta: (text) => void deltas.push(text),
    });

    expect(requests).toHaveLength(1);
    const sent = requests[0]!;
    expect(sent.method.toUpperCase()).toBe('POST');
    expect(new URL(sent.url).pathname).toBe('/v1/messages');
    // The SDK decides which header carries the key; it must be the key the transport was given.
    expect(`${sent.headers.get('x-api-key') ?? ''} ${sent.headers.get('authorization') ?? ''}`).toContain('sk-ant-test-key');
    expect(sent.headers.get('anthropic-version')).toBeTruthy();
    // The beta flag travels as a header, never in the body.
    expect(sent.headers.get('anthropic-beta')).toContain('server-side-fallback-2026-07-01');
    const body = sent.body ?? {};
    expect(body).not.toHaveProperty('betas');
    expect(body.stream).toBe(true);
    expect(body.model).toBe('claude-opus-5-5');
    expect(body.max_tokens).toBe(64000);
    expect(body.fallbacks).toBe('default');
    expect(body.thinking).toEqual({ type: 'adaptive' });
    expect(body.output_config).toEqual({ effort: 'high' });
    expect(body.tool_choice).toEqual({ type: 'auto' });
    expect(body.cache_control).toEqual({ type: 'ephemeral' });
    expect(body.system).toEqual([{ type: 'text', text: SYSTEM_PROMPT, cache_control: { type: 'ephemeral' } }]);
    for (const key of ['temperature', 'top_p', 'top_k']) expect(body).not.toHaveProperty(key);
    const tools = body.tools as { name: string; cache_control?: unknown; eager_input_streaming?: boolean; strict?: boolean }[];
    expect(tools.map((tool) => tool.name)).toEqual(['get_info', 'draw_line', 'ask_user', 'update_plan']);
    expect(tools.map((tool) => tool.cache_control)).toEqual([undefined, undefined, undefined, { type: 'ephemeral' }]);
    expect(tools.every((tool) => tool.eager_input_streaming === true && tool.strict === undefined)).toBe(true);
    expect(body.messages).toEqual([{ role: 'user', content: [{ type: 'text', text: 'Draw a line' }] }]);

    expect(deltas.join('')).toBe('Hello.');
    expect(response.stopReason).toBe('end_turn');
    expect(response.usage).toEqual({ inputTokens: 120, outputTokens: 8, cacheReadTokens: 4000, cacheCreationTokens: 0 });
  });

  it('sends no fallback parameter and no beta header for Claude Haiku 5.5', async () => {
    const { client, requests } = clientWith(() => eventStream(textTurn('claude-haiku-5-5', 'Hi.')));
    await createAnthropicTransport({ client }).run(request('claude-haiku-5-5'), {
      signal: new AbortController().signal,
      onTextDelta: () => undefined,
    });
    const sent = requests[0]!;
    expect(sent.body).not.toHaveProperty('fallbacks');
    expect(sent.body).not.toHaveProperty('betas');
    expect(sent.headers.get('anthropic-beta') ?? '').not.toContain('server-side-fallback');
    expect(sent.body?.thinking).toEqual({ type: 'adaptive' });
    expect(sent.body?.output_config).toEqual({ effort: 'high' });
  });

  it('runs a whole tool turn from real server-sent events', async () => {
    const replies = [toolTurn('claude-sonnet-5-5'), textTurn('claude-sonnet-5-5', 'The line is drawn.')];
    const { client, requests } = clientWith(() => eventStream(replies[requests.length - 1] ?? textTurn('claude-sonnet-5-5', 'Done.')));
    const cad = fakeExecutor();
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-sonnet-5-5',
      effort: 'low',
      approvalMode: 'auto',
      executor: cad,
      toolSource: FAKE_SOURCE,
      transport: createAnthropicTransport({ client }),
    });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Draw a line');

    expect(events.map((event) => event.type)).toEqual([
      'message.delta',
      'message.delta',
      'message.done',
      'tool.started',
      'tool.finished',
      'message.delta',
      'message.done',
      'turn.finished',
    ]);
    expect(events[2]).toMatchObject({ type: 'message.done', text: 'Drawing the line.' });
    expect(cad.calls[0]?.args).toEqual({ x1: 0, y1: 0, x2: 250, y2: 0, layer: '0' });
    expect(events.at(-1)).toMatchObject({
      stopReason: 'end_turn',
      model: 'claude-sonnet-5-5',
      usage: { inputTokens: 240, outputTokens: 48, cacheReadTokens: 8000, cacheCreationTokens: 0 },
    });

    // The second request replays the assistant turn unchanged, signature included, and answers
    // the tool call; the cached prefix (tools + system) is byte-identical.
    expect(requests).toHaveLength(2);
    const [first, second] = requests.map((entry) => entry.body ?? {});
    const messages = second?.messages as { role: string; content: Record<string, unknown>[] }[];
    expect(messages.map((message) => message.role)).toEqual(['user', 'assistant', 'user']);
    expect(messages[1]?.content).toEqual([
      { type: 'thinking', thinking: '', signature: 'sig-abc' },
      { type: 'text', text: 'Drawing the line.' },
      { type: 'tool_use', id: 'toolu_01', name: 'draw_line', input: { x1: 0, y1: 0, x2: 250, y2: 0 } },
    ]);
    expect(messages[2]?.content).toHaveLength(1);
    expect(messages[2]?.content[0]).toMatchObject({ type: 'tool_result', tool_use_id: 'toolu_01' });
    expect(JSON.stringify(second?.tools)).toBe(JSON.stringify(first?.tools));
    expect(JSON.stringify(second?.system)).toBe(JSON.stringify(first?.system));
    expect(second?.output_config).toEqual({ effort: 'low' });
    expect(historyProblems(session.historySnapshot())).toEqual([]);
  });

  it('turns an HTTP 401 into the invalid-key message', async () => {
    const { client } = clientWith(
      () =>
        new Response(JSON.stringify({ type: 'error', error: { type: 'authentication_error', message: 'invalid x-api-key' } }), {
          status: 401,
          headers: { 'content-type': 'application/json' },
        }),
    );
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-opus-5-5',
      effort: 'high',
      approvalMode: 'auto',
      executor: null,
      transport: createAnthropicTransport({ client }),
    });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Hi');
    expect(events.map((event) => event.type)).toEqual(['error', 'turn.finished']);
    expect(events[0]).toEqual({
      type: 'error',
      message: 'The Anthropic API key is invalid or has been revoked. Update it in Settings.',
      retryable: false,
    });
  });

  it('turns an overload reported inside the stream into a retryable error', async () => {
    const { client } = clientWith(() =>
      eventStream(
        sse([start('claude-opus-5-5')]) +
          `event: error\ndata: ${JSON.stringify({ type: 'error', error: { type: 'overloaded_error', message: 'Overloaded' } })}\n\n`,
      ),
    );
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-opus-5-5',
      effort: 'high',
      approvalMode: 'auto',
      executor: null,
      transport: createAnthropicTransport({ client }),
    });
    const events: SessionEvent[] = [];
    session.on((event) => events.push(event));
    await session.send('Hi');
    expect(events[0]).toMatchObject({ type: 'error', retryable: true });
    expect(events[0]?.type === 'error' && events[0].message).toContain('overloaded');
    expect(events.at(-1)).toMatchObject({ type: 'turn.finished', stopReason: 'error' });
  });

  it('stop() aborts the HTTP request of a running stream', async () => {
    let requestSignal: AbortSignal | null = null;
    const { client } = clientWith((_request, signal) => {
      requestSignal = signal;
      const encoder = new TextEncoder();
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(
            encoder.encode(
              sse([
                start('claude-opus-5-5'),
                { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } },
                { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: 'Thinking about' } },
              ]),
            ),
          );
          // The stream then stays open until the request is aborted.
          signal?.addEventListener('abort', () => {
            try {
              controller.error(new DOMException('The operation was aborted.', 'AbortError'));
            } catch {
              // Already closed.
            }
          });
        },
      });
      return new Response(body, { status: 200, headers: { 'content-type': 'text/event-stream' } });
    });
    const session = new CopilotSession({
      apiKey: null,
      model: 'claude-opus-5-5',
      effort: 'high',
      approvalMode: 'auto',
      executor: null,
      transport: createAnthropicTransport({ client }),
    });
    const events: SessionEvent[] = [];
    const firstDelta = new Promise<void>((resolve) => {
      session.on((event) => {
        events.push(event);
        if (event.type === 'message.delta') resolve();
      });
    });
    const turn = session.send('Hi');
    await firstDelta;
    session.stop();
    await turn;

    expect(events.map((event) => event.type)).toEqual(['message.delta', 'message.done', 'turn.finished']);
    expect(events[1]).toMatchObject({ text: 'Thinking about' });
    expect(events.at(-1)).toMatchObject({ stopReason: 'stopped' });
    expect((requestSignal as AbortSignal | null)?.aborted).toBe(true);
    expect(historyProblems(session.historySnapshot())).toEqual([]);
  });

  it('verifyApiKey makes one Models API call', async () => {
    const good = clientWith(() => new Response(JSON.stringify({ data: [], has_more: false, first_id: null, last_id: null }), { status: 200, headers: { 'content-type': 'application/json' } }));
    expect(await verifyApiKey('sk-ant-test-key', { client: good.client })).toEqual({ ok: true });
    expect(good.requests).toHaveLength(1);
    const url = new URL(good.requests[0]!.url);
    expect(good.requests[0]!.method.toUpperCase()).toBe('GET');
    expect(url.pathname).toBe('/v1/models');
    expect(url.searchParams.get('limit')).toBe('1');

    const bad = clientWith(
      () =>
        new Response(JSON.stringify({ type: 'error', error: { type: 'authentication_error', message: 'invalid x-api-key' } }), {
          status: 401,
          headers: { 'content-type': 'application/json' },
        }),
    );
    expect(await verifyApiKey('sk-ant-bad', { client: bad.client })).toEqual({
      ok: false,
      message: 'The Anthropic API key is invalid or has been revoked. Update it in Settings.',
    });
  });
});
