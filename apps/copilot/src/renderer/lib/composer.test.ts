import { describe, it, expect } from 'vitest';
import type { PickedAttachment } from '../../shared/bridge';
import { EXAMPLE_PROMPTS, METRIC_EXAMPLE_PROMPTS, examplePromptsFor, isSendKey, mergeAttachments, removeAttachment, sendBlock } from './composer';
import type { KeyLike, SendContext } from './composer';

const READY: SendContext = {
  text: 'Draw a room',
  attachmentCount: 0,
  busy: false,
  sending: false,
  hasApiKey: true,
  testModel: false,
};

function file(name: string, sizeBytes = 1000, mediaType = 'image/png'): PickedAttachment {
  return { name, mediaType, dataBase64: 'AAAA', sizeBytes };
}

function key(overrides: Partial<KeyLike> = {}): KeyLike {
  return { key: 'Enter', shiftKey: false, ctrlKey: false, altKey: false, metaKey: false, isComposing: false, ...overrides };
}

describe('sendBlock', () => {
  it('allows a message with text', () => {
    expect(sendBlock(READY)).toBeNull();
  });

  it('allows attachments without text', () => {
    expect(sendBlock({ ...READY, text: '   ', attachmentCount: 1 })).toBeNull();
  });

  it('blocks an empty draft quietly', () => {
    const block = sendBlock({ ...READY, text: '  \n ' });
    expect(block?.reason).toBe('Type a message to send.');
    expect(block?.prominent).toBe(false);
  });

  it('explains a missing API key prominently, before anything else', () => {
    const block = sendBlock({ ...READY, hasApiKey: false, busy: true, text: '' });
    expect(block?.reason).toMatch(/API key/);
    expect(block?.prominent).toBe(true);
  });

  it('does not need an API key with the test model', () => {
    expect(sendBlock({ ...READY, hasApiKey: false, testModel: true })).toBeNull();
  });

  it('blocks while a turn runs or a send is in flight', () => {
    expect(sendBlock({ ...READY, busy: true })?.reason).toMatch(/Stop/);
    expect(sendBlock({ ...READY, sending: true })?.reason).toBe('Sending...');
  });
});

describe('isSendKey', () => {
  it('sends on a plain Enter only', () => {
    expect(isSendKey(key())).toBe(true);
    expect(isSendKey(key({ shiftKey: true }))).toBe(false);
    expect(isSendKey(key({ ctrlKey: true }))).toBe(false);
    expect(isSendKey(key({ altKey: true }))).toBe(false);
    expect(isSendKey(key({ metaKey: true }))).toBe(false);
    expect(isSendKey(key({ key: 'a' }))).toBe(false);
  });

  it('never sends while an input method is composing', () => {
    expect(isSendKey(key({ isComposing: true }))).toBe(false);
  });
});

describe('mergeAttachments', () => {
  it('appends new files in order', () => {
    const merged = mergeAttachments([file('a.png')], [file('b.png'), file('c.pdf', 500, 'application/pdf')]);
    expect(merged.attachments.map((item) => item.name)).toEqual(['a.png', 'b.png', 'c.pdf']);
    expect(merged.notice).toBeNull();
  });

  it('skips a file that is already attached', () => {
    const merged = mergeAttachments([file('a.png')], [file('a.png'), file('a.png', 2000)]);
    expect(merged.attachments).toHaveLength(2);
    expect(merged.attachments[1]?.sizeBytes).toBe(2000);
    expect(merged.notice).toBeNull();
  });

  it('stops at the count limit and says so', () => {
    const merged = mergeAttachments([file('a'), file('b')], [file('c'), file('d'), file('e')], 3);
    expect(merged.attachments.map((item) => item.name)).toEqual(['a', 'b', 'c']);
    expect(merged.notice).toBe('Only 3 attachments fit in one message; 2 were left out.');
  });

  it('skips files over the size limit and says so', () => {
    const merged = mergeAttachments([], [file('big', 5000), file('ok', 100)], 8, 1024);
    expect(merged.attachments.map((item) => item.name)).toEqual(['ok']);
    expect(merged.notice).toBe('1 file is larger than 1 KB and was skipped.');
  });

  it('uses the contract limits by default', () => {
    const many = Array.from({ length: 10 }, (_, index) => file(`f${index}`));
    const merged = mergeAttachments([], many);
    expect(merged.attachments).toHaveLength(8);
    expect(merged.notice).toMatch(/Only 8 attachments/);
    const huge = mergeAttachments([], [file('huge', 11 * 1024 * 1024)]);
    expect(huge.attachments).toHaveLength(0);
    expect(huge.notice).toMatch(/10 MB/);
  });

  it('does not mutate its inputs', () => {
    const current = [file('a')];
    const picked = [file('b')];
    mergeAttachments(current, picked);
    expect(current).toHaveLength(1);
    expect(picked).toHaveLength(1);
  });
});

describe('removeAttachment', () => {
  it('removes by index and ignores bad indexes', () => {
    const files = [file('a'), file('b'), file('c')];
    expect(removeAttachment(files, 1).map((item) => item.name)).toEqual(['a', 'c']);
    expect(removeAttachment(files, 5)).toEqual(files);
    expect(removeAttachment(files, -1)).toEqual(files);
    expect(removeAttachment(files, 0.5)).toEqual(files);
    expect(files).toHaveLength(3);
  });
});

describe('EXAMPLE_PROMPTS', () => {
  it('offers three or four distinct prompts', () => {
    expect(EXAMPLE_PROMPTS.length).toBeGreaterThanOrEqual(3);
    expect(EXAMPLE_PROMPTS.length).toBeLessThanOrEqual(4);
    expect(new Set(EXAMPLE_PROMPTS).size).toBe(EXAMPLE_PROMPTS.length);
  });
});

describe('examplePromptsFor', () => {
  it('uses metric sizes for metric drawings and imperial ones otherwise', () => {
    for (const unit of ['mm', 'cm', 'm']) expect(examplePromptsFor(unit)).toBe(METRIC_EXAMPLE_PROMPTS);
    for (const unit of ['in', 'ft', null, undefined, 'yards']) expect(examplePromptsFor(unit)).toBe(EXAMPLE_PROMPTS);
  });
  it('offers the same number of distinct metric prompts, none in feet', () => {
    expect(METRIC_EXAMPLE_PROMPTS).toHaveLength(EXAMPLE_PROMPTS.length);
    expect(new Set(METRIC_EXAMPLE_PROMPTS).size).toBe(METRIC_EXAMPLE_PROMPTS.length);
    expect(METRIC_EXAMPLE_PROMPTS.some((prompt) => /\bft\b/.test(prompt))).toBe(false);
  });
});
