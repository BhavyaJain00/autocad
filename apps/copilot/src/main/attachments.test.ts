import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ATTACHMENT_MAX_BYTES, ATTACHMENT_MAX_COUNT } from '../shared/bridge';
import {
  ATTACHMENT_DIALOG_FILTERS,
  ATTACHMENT_MEDIA_TYPES,
  ATTACHMENT_NAME_MAX_LENGTH,
  attachmentInfo,
  base64DecodedSize,
  checkAttachments,
  mediaTypeForFile,
  readPickedAttachments,
} from './attachments';

describe('mediaTypeForFile', () => {
  it('classifies the supported extensions', () => {
    const expected: Record<string, string> = {
      'plan.png': 'image/png',
      'photo.jpg': 'image/jpeg',
      'photo.jpeg': 'image/jpeg',
      'sketch.webp': 'image/webp',
      'anim.gif': 'image/gif',
      'brief.pdf': 'application/pdf',
      'notes.txt': 'text/plain',
      'README.md': 'text/markdown',
      'rooms.csv': 'text/csv',
      'data.json': 'application/json',
    };
    for (const [name, mediaType] of Object.entries(expected)) {
      expect(mediaTypeForFile(name), name).toBe(mediaType);
      expect(ATTACHMENT_MEDIA_TYPES.has(mediaType)).toBe(true);
    }
  });

  it('ignores case and uses only the last extension', () => {
    expect(mediaTypeForFile('PLAN.PNG')).toBe('image/png');
    expect(mediaTypeForFile('Scan.Pdf')).toBe('application/pdf');
    expect(mediaTypeForFile('archive.tar.json')).toBe('application/json');
    expect(mediaTypeForFile('C:\\Users\\me\\Site Plan.final.JPG')).toBe('image/jpeg');
  });

  it('rejects everything else', () => {
    for (const name of ['drawing.dwg', 'model.cadstudio', 'run.exe', 'page.html', 'vector.svg', 'noextension', '.png', 'png', 'a.', 'x.constructor', 'x.toString', '']) {
      expect(mediaTypeForFile(name), name).toBeNull();
    }
  });

  it('offers every supported extension in the dialog filters', () => {
    const all = ATTACHMENT_DIALOG_FILTERS[0]?.extensions ?? [];
    expect([...all].sort()).toEqual(['csv', 'gif', 'jpeg', 'jpg', 'json', 'md', 'pdf', 'png', 'txt', 'webp']);
    for (const extension of all) expect(mediaTypeForFile(`file.${extension}`)).not.toBeNull();
    for (const filter of ATTACHMENT_DIALOG_FILTERS.slice(1)) {
      for (const extension of filter.extensions) expect(all).toContain(extension);
    }
  });
});

describe('readPickedAttachments', () => {
  let directory: string;
  let logs: string[];
  const log = (message: string): void => {
    logs.push(message);
  };
  const file = (name: string, content: string | Buffer): string => {
    const filePath = path.join(directory, name);
    writeFileSync(filePath, content);
    return filePath;
  };

  beforeEach(() => {
    directory = mkdtempSync(path.join(os.tmpdir(), 'copilot-attachments-'));
    logs = [];
  });

  afterEach(() => {
    rmSync(directory, { recursive: true, force: true });
  });

  it('reads supported files with their media type, size and base64 content, in order', async () => {
    const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    const picked = await readPickedAttachments([file('plan.PNG', png), file('notes.txt', 'wall 200 mm')], { log });
    expect(picked).toEqual([
      { name: 'plan.PNG', mediaType: 'image/png', dataBase64: png.toString('base64'), sizeBytes: png.byteLength },
      { name: 'notes.txt', mediaType: 'text/plain', dataBase64: Buffer.from('wall 200 mm').toString('base64'), sizeBytes: 11 },
    ]);
    expect(logs).toEqual([]);
  });

  it('skips unsupported, empty, missing and oversized files and directories, and logs each', async () => {
    mkdirSync(path.join(directory, 'folder.png'));
    const picked = await readPickedAttachments(
      [
        file('drawing.dwg', 'x'),
        file('empty.txt', ''),
        path.join(directory, 'missing.pdf'),
        file('big.csv', 'a'.repeat(33)),
        path.join(directory, 'folder.png'),
        file('ok.md', '# ok'),
      ],
      { log, maxBytes: 32 },
    );
    expect(picked.map((attachment) => attachment.name)).toEqual(['ok.md']);
    expect(logs).toHaveLength(5);
    expect(logs[0]).toContain('drawing.dwg');
    expect(logs[1]).toContain('empty.txt');
    expect(logs[2]).toContain('missing.pdf');
    expect(logs[3]).toContain('big.csv');
    expect(logs[4]).toContain('folder.png');
  });

  it('accepts a file of exactly the size limit', async () => {
    const picked = await readPickedAttachments([file('exact.txt', 'a'.repeat(32))], { log, maxBytes: 32 });
    expect(picked).toHaveLength(1);
    expect(picked[0]?.sizeBytes).toBe(32);
  });

  it('keeps the first files up to the count limit and logs the rest', async () => {
    const paths = Array.from({ length: 5 }, (_, index) => file(`n${index}.txt`, `file ${index}`));
    const picked = await readPickedAttachments(paths, { log, maxCount: 3 });
    expect(picked.map((attachment) => attachment.name)).toEqual(['n0.txt', 'n1.txt', 'n2.txt']);
    expect(logs).toHaveLength(2);
    expect(logs[0]).toContain('n3.txt');
  });

  it('does not let skipped files count against the limit', async () => {
    const picked = await readPickedAttachments(
      [file('a.exe', 'x'), file('b.txt', 'b'), file('c.exe', 'x'), file('d.txt', 'd')],
      { log, maxCount: 2 },
    );
    expect(picked.map((attachment) => attachment.name)).toEqual(['b.txt', 'd.txt']);
  });

  it('returns nothing for an empty selection', async () => {
    expect(await readPickedAttachments([])).toEqual([]);
  });
});

describe('base64DecodedSize', () => {
  it('computes the decoded size of padded base64', () => {
    for (const length of [0, 1, 2, 3, 4, 5, 100, 1023]) {
      const text = Buffer.alloc(length, 7).toString('base64');
      expect(base64DecodedSize(text), String(length)).toBe(length);
    }
  });

  it('rejects text that is not canonical base64', () => {
    for (const text of ['abc', 'ab=c', 'a b=', 'ab\n=', '====', 'YWJj*A==', 'data:image/png;base64,AAAA', 'YQ===', '-_8=']) {
      expect(base64DecodedSize(text), text).toBeNull();
    }
  });
});

describe('checkAttachments', () => {
  const valid = (overrides: Record<string, unknown> = {}): Record<string, unknown> => ({
    name: 'notes.txt',
    mediaType: 'text/plain',
    dataBase64: Buffer.from('hello').toString('base64'),
    sizeBytes: 5,
    ...overrides,
  });

  it('accepts valid attachments and recomputes their size', () => {
    const result = checkAttachments([valid({ sizeBytes: 1 }), valid({ name: 'p.png', mediaType: 'image/png', extra: true })]);
    expect(result).toEqual({
      ok: true,
      attachments: [
        { name: 'notes.txt', mediaType: 'text/plain', dataBase64: 'aGVsbG8=', sizeBytes: 5 },
        { name: 'p.png', mediaType: 'image/png', dataBase64: 'aGVsbG8=', sizeBytes: 5 },
      ],
    });
  });

  it('accepts an empty list', () => {
    expect(checkAttachments([])).toEqual({ ok: true, attachments: [] });
  });

  it('rejects anything that is not a list', () => {
    for (const value of [undefined, null, 'file', 5, {}, { length: 1, 0: valid() }]) {
      expect(checkAttachments(value).ok).toBe(false);
    }
  });

  it('rejects more attachments than allowed', () => {
    const many = Array.from({ length: ATTACHMENT_MAX_COUNT + 1 }, () => valid());
    expect(checkAttachments(many).ok).toBe(false);
    expect(checkAttachments(many.slice(1)).ok).toBe(true);
  });

  it('rejects invalid items', () => {
    const invalid: unknown[] = [
      null,
      'notes.txt',
      valid({ name: '' }),
      valid({ name: '   ' }),
      valid({ name: 5 }),
      valid({ name: 'n'.repeat(ATTACHMENT_NAME_MAX_LENGTH + 1) }),
      valid({ mediaType: 'application/x-msdownload' }),
      valid({ mediaType: 'text/html' }),
      valid({ mediaType: undefined }),
      valid({ dataBase64: undefined }),
      valid({ dataBase64: 12345 }),
      valid({ dataBase64: '' }),
      valid({ dataBase64: 'not base64!' }),
      valid({ dataBase64: 'aGVsbG8' }),
    ];
    for (const item of invalid) {
      const result = checkAttachments([item]);
      expect(result.ok, JSON.stringify(item)?.slice(0, 60)).toBe(false);
      if (!result.ok) expect(result.message.length).toBeGreaterThan(0);
    }
  });

  it('enforces the size limit on the decoded data', () => {
    const atLimit = Buffer.alloc(ATTACHMENT_MAX_BYTES, 1).toString('base64');
    const overLimit = Buffer.alloc(ATTACHMENT_MAX_BYTES + 1, 1).toString('base64');
    const accepted = checkAttachments([valid({ dataBase64: atLimit })]);
    expect(accepted.ok).toBe(true);
    if (accepted.ok) expect(accepted.attachments[0]?.sizeBytes).toBe(ATTACHMENT_MAX_BYTES);
    expect(checkAttachments([valid({ dataBase64: overLimit })]).ok).toBe(false);
  });
});

describe('attachmentInfo', () => {
  it('describes an attachment without its content', () => {
    expect(attachmentInfo({ name: 'a.pdf', mediaType: 'application/pdf', dataBase64: 'AAAA', sizeBytes: 3 })).toEqual({
      name: 'a.pdf',
      mediaType: 'application/pdf',
      sizeBytes: 3,
    });
  });
});
