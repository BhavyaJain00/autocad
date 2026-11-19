import { describe, it, expect } from 'vitest';
import { MAX_TEXT_ATTACHMENT_CHARS, mapAttachments } from './attachments';

const b64 = (text: string): string => Buffer.from(text, 'utf8').toString('base64');
const IMAGE = b64('binary-image-bytes');

describe('mapAttachments', () => {
  it('maps every supported image type to an image block', () => {
    for (const mediaType of ['image/png', 'image/jpeg', 'image/webp', 'image/gif'] as const) {
      const mapped = mapAttachments([{ name: 'a', mediaType, dataBase64: IMAGE }]);
      expect(mapped).toEqual({
        ok: true,
        blocks: [{ type: 'image', source: { type: 'base64', media_type: mediaType, data: IMAGE } }],
      });
    }
  });

  it('normalises the media type (case, parameters, image/jpg)', () => {
    const mapped = mapAttachments([
      { name: 'a.jpg', mediaType: 'IMAGE/JPG', dataBase64: IMAGE },
      { name: 'b.png', mediaType: 'image/png; charset=binary', dataBase64: IMAGE },
    ]);
    expect(mapped.ok && mapped.blocks).toEqual([
      { type: 'image', source: { type: 'base64', media_type: 'image/jpeg', data: IMAGE } },
      { type: 'image', source: { type: 'base64', media_type: 'image/png', data: IMAGE } },
    ]);
  });

  it('maps a PDF to a document block titled with the file name', () => {
    const data = b64('%PDF-1.7');
    expect(mapAttachments([{ name: 'Brief.pdf', mediaType: 'application/pdf', dataBase64: data }])).toEqual({
      ok: true,
      blocks: [{ type: 'document', source: { type: 'base64', media_type: 'application/pdf', data }, title: 'Brief.pdf' }],
    });
  });

  it('maps text files to text blocks with a file-name header', () => {
    const mapped = mapAttachments([
      { name: 'rooms.txt', mediaType: 'text/plain', dataBase64: b64('Küche 3 × 4 m') },
      { name: 'list.csv', mediaType: 'text/csv', dataBase64: b64('a,b\n1,2') },
      { name: 'data.json', mediaType: 'application/json', dataBase64: b64('{"a":1}') },
      { name: 'old.dxf', mediaType: 'image/vnd.dxf', dataBase64: b64('0\nSECTION') },
      { name: 'bom.txt', mediaType: 'text/plain', dataBase64: b64('﻿with bom') },
    ]);
    expect(mapped.ok && mapped.blocks).toEqual([
      { type: 'text', text: 'Attached file "rooms.txt":\n\nKüche 3 × 4 m' },
      { type: 'text', text: 'Attached file "list.csv":\n\na,b\n1,2' },
      { type: 'text', text: 'Attached file "data.json":\n\n{"a":1}' },
      { type: 'text', text: 'Attached file "old.dxf":\n\n0\nSECTION' },
      { type: 'text', text: 'Attached file "bom.txt":\n\nwith bom' },
    ]);
  });

  it('keeps the order of mixed attachments', () => {
    const mapped = mapAttachments([
      { name: 'n.txt', mediaType: 'text/plain', dataBase64: b64('note') },
      { name: 'p.png', mediaType: 'image/png', dataBase64: IMAGE },
      { name: 'd.pdf', mediaType: 'application/pdf', dataBase64: IMAGE },
    ]);
    expect(mapped.ok && mapped.blocks.map((block) => block.type)).toEqual(['text', 'image', 'document']);
    expect(mapAttachments([])).toEqual({ ok: true, blocks: [] });
  });

  it('accepts data URLs and base64 with line breaks', () => {
    const mapped = mapAttachments([
      { name: 'a.png', mediaType: 'image/png', dataBase64: `data:image/png;base64,${IMAGE}` },
      { name: 'b.png', mediaType: 'image/png', dataBase64: `${IMAGE.slice(0, 8)}\r\n${IMAGE.slice(8)}` },
    ]);
    expect(mapped.ok && mapped.blocks).toEqual([
      { type: 'image', source: { type: 'base64', media_type: 'image/png', data: IMAGE } },
      { type: 'image', source: { type: 'base64', media_type: 'image/png', data: IMAGE } },
    ]);
  });

  it('cuts very long text files and says so', () => {
    const mapped = mapAttachments([
      { name: 'big.txt', mediaType: 'text/plain', dataBase64: b64('a'.repeat(MAX_TEXT_ATTACHMENT_CHARS + 500)) },
    ]);
    expect(mapped.ok).toBe(true);
    const block = mapped.ok ? mapped.blocks[0] : undefined;
    const text = block !== undefined && block.type === 'text' ? block.text : '';
    expect(text).toContain('500 more characters were cut');
    expect(text.length).toBeLessThan(MAX_TEXT_ATTACHMENT_CHARS + 200);
  });

  it('rejects unsupported types with a message that names the file', () => {
    expect(mapAttachments([{ name: 'plan.dwg', mediaType: 'application/acad', dataBase64: IMAGE }])).toEqual({
      ok: false,
      message:
        '"plan.dwg" (application/acad) is not a supported attachment. Attach images (PNG, JPEG, WebP, GIF), PDF files or text files.',
    });
    expect(mapAttachments([{ name: 'clip.mp4', mediaType: 'video/mp4', dataBase64: IMAGE }]).ok).toBe(false);
    expect(mapAttachments([{ name: 'x.bmp', mediaType: 'image/bmp', dataBase64: IMAGE }]).ok).toBe(false);
    const missing = mapAttachments([{ name: '', mediaType: '', dataBase64: IMAGE }]);
    expect(missing).toEqual({
      ok: false,
      message:
        '"attachment" is not a supported attachment. Attach images (PNG, JPEG, WebP, GIF), PDF files or text files.',
    });
  });

  it('fails the whole mapping when one attachment is unsupported', () => {
    const mapped = mapAttachments([
      { name: 'ok.png', mediaType: 'image/png', dataBase64: IMAGE },
      { name: 'bad.zip', mediaType: 'application/zip', dataBase64: IMAGE },
    ]);
    expect(mapped.ok).toBe(false);
    expect(!mapped.ok && mapped.message).toContain('bad.zip');
  });

  it('rejects empty or damaged data', () => {
    for (const dataBase64 of ['', '   ', 'not base64!', 'data:image/png;base64', 'data:image/png;base64,']) {
      expect(mapAttachments([{ name: 'a.png', mediaType: 'image/png', dataBase64 }])).toEqual({
        ok: false,
        message: '"a.png" could not be read (it is empty or damaged). Attach it again.',
      });
    }
    expect(mapAttachments([{ name: 'a.png', mediaType: 'image/png', dataBase64: 42 as never }]).ok).toBe(false);
  });
});
