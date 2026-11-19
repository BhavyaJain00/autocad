// Mapping user attachments to message content blocks: images, PDFs and text files.
import { Buffer } from 'node:buffer';
import type {
  BetaContentBlockParam,
  BetaImageBlockParam,
  BetaRequestDocumentBlock,
  BetaTextBlockParam,
} from '@anthropic-ai/sdk/resources/beta/messages/messages';
import type { Attachment } from './types';

type ImageMediaType = 'image/png' | 'image/jpeg' | 'image/webp' | 'image/gif';

const IMAGE_MEDIA_TYPES: Readonly<Record<string, ImageMediaType>> = {
  'image/png': 'image/png',
  'image/jpeg': 'image/jpeg',
  'image/jpg': 'image/jpeg',
  'image/webp': 'image/webp',
  'image/gif': 'image/gif',
};

/** Media types that are text even though they do not start with `text/`. */
const TEXT_MEDIA_TYPES: ReadonlySet<string> = new Set([
  'application/json',
  'application/x-ndjson',
  'application/xml',
  'application/yaml',
  'application/x-yaml',
  'application/csv',
  'application/dxf',
  'application/x-dxf',
  'image/vnd.dxf',
  'image/svg+xml',
]);

/** Longest text attachment passed to the model in full (characters); longer files are cut, with a note. */
export const MAX_TEXT_ATTACHMENT_CHARS = 200_000;

/** What the model can read, for error messages. */
const SUPPORTED_HINT = 'Attach images (PNG, JPEG, WebP, GIF), PDF files or text files.';

/** Result of mapping the attachments of one user message. */
export type AttachmentMapping =
  | { ok: true; blocks: BetaContentBlockParam[] }
  | { ok: false; message: string };

function normaliseMediaType(mediaType: unknown): string {
  if (typeof mediaType !== 'string') return '';
  // Drop parameters such as "; charset=utf-8".
  return (mediaType.split(';')[0] ?? '').trim().toLowerCase();
}

function displayName(name: unknown): string {
  const text = typeof name === 'string' ? name.replace(/[\r\n\t]+/g, ' ').trim() : '';
  if (text.length === 0) return 'attachment';
  return text.length > 200 ? `${text.slice(0, 197)}...` : text;
}

/** Strips a `data:` URL prefix and whitespace; returns null when the rest is not base64. */
function cleanBase64(data: unknown): string | null {
  if (typeof data !== 'string') return null;
  let text = data.trim();
  if (text.startsWith('data:')) {
    const comma = text.indexOf(',');
    if (comma === -1) return null;
    text = text.slice(comma + 1);
  }
  text = text.replace(/\s+/g, '');
  if (text.length === 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(text)) return null;
  return text;
}

function isTextMediaType(mediaType: string): boolean {
  return mediaType.startsWith('text/') || TEXT_MEDIA_TYPES.has(mediaType);
}

/**
 * Maps attachments to content blocks in the given order: images become image blocks, PDFs become
 * document blocks, text files become text blocks that start with a file-name header. Any
 * unsupported or unreadable attachment fails the whole mapping with a message for the user.
 */
export function mapAttachments(attachments: readonly Attachment[]): AttachmentMapping {
  const blocks: BetaContentBlockParam[] = [];
  for (const attachment of attachments) {
    const name = displayName(attachment?.name);
    const mediaType = normaliseMediaType(attachment?.mediaType);
    const imageType = IMAGE_MEDIA_TYPES[mediaType];
    const isPdf = mediaType === 'application/pdf';
    if (imageType === undefined && !isPdf && !isTextMediaType(mediaType)) {
      const kind = mediaType.length > 0 ? ` (${mediaType})` : '';
      return { ok: false, message: `"${name}"${kind} is not a supported attachment. ${SUPPORTED_HINT}` };
    }
    const data = cleanBase64(attachment?.dataBase64);
    if (data === null) {
      return { ok: false, message: `"${name}" could not be read (it is empty or damaged). Attach it again.` };
    }
    if (imageType !== undefined) {
      const block: BetaImageBlockParam = {
        type: 'image',
        source: { type: 'base64', media_type: imageType, data },
      };
      blocks.push(block);
    } else if (isPdf) {
      const block: BetaRequestDocumentBlock = {
        type: 'document',
        source: { type: 'base64', media_type: 'application/pdf', data },
        title: name,
      };
      blocks.push(block);
    } else {
      let text = Buffer.from(data, 'base64').toString('utf8').replace(/^﻿/, '');
      let note = '';
      if (text.length > MAX_TEXT_ATTACHMENT_CHARS) {
        note = `\n\n[The file is longer than this: ${text.length - MAX_TEXT_ATTACHMENT_CHARS} more characters were cut.]`;
        text = text.slice(0, MAX_TEXT_ATTACHMENT_CHARS);
      }
      const block: BetaTextBlockParam = {
        type: 'text',
        text: `Attached file "${name}":\n\n${text}${note}`,
      };
      blocks.push(block);
    }
  }
  return { ok: true, blocks };
}
