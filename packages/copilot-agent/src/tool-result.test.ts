import { describe, it, expect } from 'vitest';
import type { ToolResult } from '@cadstudio/cad-protocol';
import { MAX_TOOL_RESULT_CHARS, errorResultText, serializeForModel, serializeToolResult } from './tool-result';

function result(data: unknown, extra: Partial<ToolResult> = {}): ToolResult {
  return { success: true, data, createdIds: [], modifiedIds: [], deletedIds: [], warnings: [], errors: [], revision: 12, ...extra };
}

describe('serializeToolResult', () => {
  it('returns compact JSON of a small result unchanged', () => {
    const value = result({ layers: [{ name: '0' }] }, { createdIds: ['a', 'b'] });
    const text = serializeToolResult(value);
    expect(text).toBe(JSON.stringify(value));
    expect(text).not.toContain('\n');
    expect(JSON.parse(text)).toEqual(value);
  });

  it('caps a large result at about 60 KB by cutting lists, and says what was cut', () => {
    expect(MAX_TOOL_RESULT_CHARS).toBe(60_000);
    const entities = Array.from({ length: 4000 }, (_, index) => ({ id: `e${index}`, type: 'line', start: { x: index, y: 0 }, end: { x: index, y: 100 } }));
    const ids = entities.map((entity) => entity.id);
    const text = serializeToolResult(result({ entities, total: 4000 }, { createdIds: ids }));

    expect(text.length).toBeLessThanOrEqual(MAX_TOOL_RESULT_CHARS);
    expect(text.length).toBeGreaterThan(MAX_TOOL_RESULT_CHARS * 0.8);
    const parsed = JSON.parse(text) as ToolResult & { data: { entities: unknown[]; total: number }; truncated: { note: string; lists: string[] } };
    expect(parsed.success).toBe(true);
    expect(parsed.revision).toBe(12);
    expect(parsed.data.total).toBe(4000);
    const kept = parsed.data.entities.length;
    expect(kept).toBeGreaterThan(0);
    expect(kept).toBeLessThan(4000);
    // The first items are kept, in order, and every list is cut to the same length.
    expect(parsed.data.entities[0]).toEqual(entities[0]);
    expect(parsed.data.entities[kept - 1]).toEqual(entities[kept - 1]);
    expect(parsed.createdIds).toEqual(ids.slice(0, kept));
    expect(parsed.truncated.note).toContain('too large');
    expect(parsed.truncated.lists).toEqual([`data.entities: ${kept} of 4000`, `createdIds: ${kept} of 4000`]);
  });

  it('keeps as many items as fit (one more would not)', () => {
    const items = Array.from({ length: 300 }, (_, index) => `item-${String(index).padStart(4, '0')}`);
    const text = serializeForModel({ items }, 1000);
    expect(text.length).toBeLessThanOrEqual(1000);
    const kept = (JSON.parse(text) as { items: string[] }).items.length;
    // Each further item costs 12 characters ("item-0000",) and would push it past the limit.
    expect(text.length + 12 + 2).toBeGreaterThan(1000);
    expect(kept).toBeGreaterThan(30);
  });

  it('cuts nested lists too and limits how many it names', () => {
    const rooms = Array.from({ length: 40 }, (_, room) => ({ name: `Room ${room}`, points: Array.from({ length: 400 }, (_, index) => ({ x: index, y: room })) }));
    const text = serializeForModel(result({ rooms }), 20_000);
    expect(text.length).toBeLessThanOrEqual(20_000);
    const parsed = JSON.parse(text) as { data: { rooms: { points: unknown[] }[] }; truncated: { lists: string[] } };
    expect(parsed.data.rooms.length).toBeGreaterThan(0);
    const limit = parsed.data.rooms[0]?.points.length ?? 0;
    expect(parsed.data.rooms.every((room) => room.points.length === limit)).toBe(true);
    expect(parsed.truncated.lists.length).toBeLessThanOrEqual(13);
    expect(parsed.truncated.lists[0]).toMatch(/^data\.rooms(\[0\]\.points)?: \d+ of \d+$/);
  });

  it('shortens very long texts when cutting lists is not enough', () => {
    const text = serializeForModel(result({ note: 'x'.repeat(200_000), items: [1, 2, 3] }));
    expect(text.length).toBeLessThanOrEqual(MAX_TOOL_RESULT_CHARS);
    const parsed = JSON.parse(text) as { data: { note: string; items: number[] }; truncated: { note: string } };
    expect(parsed.data.note.length).toBeLessThan(2200);
    expect(parsed.data.note).toContain('more characters cut');
    expect(parsed.data.items).toEqual([1, 2, 3]);
    expect(parsed.truncated.note).toContain('long texts were shortened');
  });

  it('falls back to an honest minimal envelope when nothing structured fits', () => {
    const wide: Record<string, number> = {};
    for (let index = 0; index < 20_000; index += 1) wide[`key${index}`] = index;
    const text = serializeForModel(result(wide, { success: false }), 5000);
    expect(text.length).toBeLessThanOrEqual(5000);
    const parsed = JSON.parse(text) as { success: boolean; revision: number; truncated: { note: string } };
    expect(parsed.success).toBe(false);
    expect(parsed.revision).toBe(12);
    expect(parsed.truncated.note).toContain('too large to return');
  });

  it('serialises non-object values and undefined', () => {
    expect(serializeForModel(undefined)).toBe('null');
    expect(serializeForModel('ok')).toBe('"ok"');
    const list = Array.from({ length: 1000 }, (_, index) => index);
    const text = serializeForModel(list, 500);
    expect(text.length).toBeLessThanOrEqual(500);
    expect((JSON.parse(text) as { value: number[] }).value[0]).toBe(0);
  });
});

describe('errorResultText', () => {
  it('lists problems as bullet lines', () => {
    expect(errorResultText('Invalid arguments:', ['a: is required', 'b: too small'])).toBe('Invalid arguments:\n- a: is required\n- b: too small');
    expect(errorResultText('Just this.')).toBe('Just this.');
  });
});
