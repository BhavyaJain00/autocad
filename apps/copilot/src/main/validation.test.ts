import { describe, expect, it } from 'vitest';
import { MESSAGE_MAX_LENGTH } from './constants';
import {
  ANSWER_MAX_LENGTH,
  ANSWERS_MAX_COUNT,
  checkAnswers,
  checkConnectorId,
  checkDecision,
  checkExternalUrl,
  checkMessage,
  checkRequestId,
  ID_MAX_LENGTH,
  URL_MAX_LENGTH,
} from './validation';

const attachment = { name: 'a.txt', mediaType: 'text/plain', dataBase64: 'aGk=', sizeBytes: 2 };

describe('checkMessage', () => {
  it('accepts text and trims it', () => {
    expect(checkMessage('  draw a 4 x 3 m room \n', [])).toEqual({ ok: true, text: 'draw a 4 x 3 m room', attachments: [] });
  });

  it('accepts blank text when files are attached', () => {
    expect(checkMessage('   ', [attachment])).toEqual({ ok: true, text: '', attachments: [attachment] });
  });

  it('treats missing attachments as none', () => {
    expect(checkMessage('hello', undefined)).toEqual({ ok: true, text: 'hello', attachments: [] });
    expect(checkMessage('hello', null)).toEqual({ ok: true, text: 'hello', attachments: [] });
  });

  it('rejects a blank message without attachments', () => {
    for (const text of ['', '   ', '\n\t ']) {
      expect(checkMessage(text, [])).toEqual({ ok: false, message: 'Type a message or attach a file first.' });
    }
  });

  it('rejects text that is not a string', () => {
    for (const text of [undefined, null, 5, {}, ['hello']]) {
      expect(checkMessage(text, []).ok).toBe(false);
    }
  });

  it('enforces the length limit on the raw text', () => {
    expect(checkMessage('a'.repeat(MESSAGE_MAX_LENGTH), []).ok).toBe(true);
    const tooLong = checkMessage('a'.repeat(MESSAGE_MAX_LENGTH + 1), []);
    expect(tooLong.ok).toBe(false);
    if (!tooLong.ok) expect(tooLong.message).toContain('100,000');
    // Padding with spaces does not get around the limit.
    expect(checkMessage(`a${' '.repeat(MESSAGE_MAX_LENGTH)}`, []).ok).toBe(false);
  });

  it('rejects invalid attachments with their reason', () => {
    const result = checkMessage('see file', [{ ...attachment, mediaType: 'text/html' }]);
    expect(result.ok).toBe(false);
    expect(checkMessage('see file', 'a.txt').ok).toBe(false);
  });
});

describe('checkRequestId', () => {
  it('accepts non-empty strings of bounded length', () => {
    expect(checkRequestId('q-1')).toBe('q-1');
    expect(checkRequestId('x'.repeat(ID_MAX_LENGTH))).toHaveLength(ID_MAX_LENGTH);
  });

  it('rejects everything else', () => {
    for (const value of ['', 'x'.repeat(ID_MAX_LENGTH + 1), 5, null, undefined, {}, ['q-1']]) {
      expect(checkRequestId(value)).toBeNull();
    }
  });
});

describe('checkAnswers', () => {
  it('accepts answers and drops extra properties', () => {
    expect(checkAnswers([{ questionId: 'wall', answer: '200 mm', injected: true }, { questionId: 'door', answer: '' }])).toEqual([
      { questionId: 'wall', answer: '200 mm' },
      { questionId: 'door', answer: '' },
    ]);
    expect(checkAnswers([])).toEqual([]);
  });

  it('rejects malformed answers', () => {
    const invalid: unknown[] = [
      undefined,
      null,
      'answer',
      { questionId: 'a', answer: 'b' },
      [null],
      ['a'],
      [{ questionId: '', answer: 'x' }],
      [{ questionId: 5, answer: 'x' }],
      [{ questionId: 'a' }],
      [{ questionId: 'a', answer: 5 }],
      [{ questionId: 'a', answer: 'x'.repeat(ANSWER_MAX_LENGTH + 1) }],
      Array.from({ length: ANSWERS_MAX_COUNT + 1 }, () => ({ questionId: 'a', answer: 'b' })),
    ];
    for (const value of invalid) expect(checkAnswers(value), JSON.stringify(value)?.slice(0, 50)).toBeNull();
  });
});

describe('checkDecision', () => {
  it('accepts only the three decisions', () => {
    expect(checkDecision('approve')).toBe('approve');
    expect(checkDecision('reject')).toBe('reject');
    expect(checkDecision('approve-all')).toBe('approve-all');
    for (const value of ['Approve', 'yes', '', true, null, undefined, 1]) expect(checkDecision(value)).toBeNull();
  });
});

describe('checkConnectorId', () => {
  it('accepts advert ids', () => {
    expect(checkConnectorId('cadstudio-1234')).toBe('cadstudio-1234');
    expect(checkConnectorId('autocad-98765')).toBe('autocad-98765');
  });

  it('rejects path-like, empty, long and non-string values', () => {
    for (const value of ['', '../secrets', 'a/b', 'a\\b', 'id with space', 'x'.repeat(129), 'id\n', 12, null, undefined]) {
      expect(checkConnectorId(value), String(value).slice(0, 20)).toBeNull();
    }
  });
});

describe('checkExternalUrl', () => {
  it('accepts https URLs and normalises them', () => {
    expect(checkExternalUrl('https://console.anthropic.com/settings/keys')).toBe('https://console.anthropic.com/settings/keys');
    expect(checkExternalUrl('HTTPS://Example.com')).toBe('https://example.com/');
    expect(checkExternalUrl('https://example.com/a?b=c#d')).toBe('https://example.com/a?b=c#d');
  });

  it('rejects everything that is not a plain https URL', () => {
    const invalid: unknown[] = [
      'http://example.com/',
      'file:///C:/Windows/System32/calc.exe',
      'javascript:alert(1)',
      'ms-settings:privacy',
      'mailto:someone@example.com',
      'data:text/html,<script>1</script>',
      'C:\\Windows\\System32\\calc.exe',
      '\\\\server\\share\\run.bat',
      'example.com',
      '//example.com',
      'https://',
      'https://user:secret@example.com/',
      'https://user@example.com/',
      '',
      ' https://example.com with spaces',
      `https://example.com/${'a'.repeat(URL_MAX_LENGTH)}`,
      null,
      undefined,
      42,
      { href: 'https://example.com' },
    ];
    for (const value of invalid) expect(checkExternalUrl(value), String(value).slice(0, 40)).toBeNull();
  });
});
