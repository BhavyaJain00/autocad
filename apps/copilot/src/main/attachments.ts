/**
 * Message attachments: which files are accepted, reading the files the user picked in the native
 * dialog, and validating attachments that come back from the renderer with a message.
 * No Electron imports (the dialog itself is opened by the caller).
 */
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { ATTACHMENT_MAX_BYTES, ATTACHMENT_MAX_COUNT, type AttachmentInfo, type PickedAttachment } from '../shared/bridge';
import { errorMessage } from './json-file';

/** Media type by lower-case file extension (without the dot). */
const MEDIA_TYPE_BY_EXTENSION: Readonly<Record<string, string>> = {
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  webp: 'image/webp',
  gif: 'image/gif',
  pdf: 'application/pdf',
  txt: 'text/plain',
  md: 'text/markdown',
  csv: 'text/csv',
  json: 'application/json',
};

export const IMAGE_EXTENSIONS: readonly string[] = ['png', 'jpg', 'jpeg', 'webp', 'gif'];
export const DOCUMENT_EXTENSIONS: readonly string[] = ['pdf'];
export const TEXT_EXTENSIONS: readonly string[] = ['txt', 'md', 'csv', 'json'];

/** Every media type an attachment may have. */
export const ATTACHMENT_MEDIA_TYPES: ReadonlySet<string> = new Set(Object.values(MEDIA_TYPE_BY_EXTENSION));

/** File-type filters of the native "attach files" dialog. */
export const ATTACHMENT_DIALOG_FILTERS: { name: string; extensions: string[] }[] = [
  { name: 'Images, PDF and text files', extensions: [...IMAGE_EXTENSIONS, ...DOCUMENT_EXTENSIONS, ...TEXT_EXTENSIONS] },
  { name: 'Images', extensions: [...IMAGE_EXTENSIONS] },
  { name: 'PDF documents', extensions: [...DOCUMENT_EXTENSIONS] },
  { name: 'Text files', extensions: [...TEXT_EXTENSIONS] },
];

/** Longest attachment name kept (characters). */
export const ATTACHMENT_NAME_MAX_LENGTH = 255;

/** The media type of a file by its extension (case-insensitive), or null when it is not accepted. */
export function mediaTypeForFile(fileName: string): string | null {
  const extension = path.extname(fileName).slice(1).toLowerCase();
  if (extension.length === 0) return null;
  return Object.hasOwn(MEDIA_TYPE_BY_EXTENSION, extension) ? (MEDIA_TYPE_BY_EXTENSION[extension] ?? null) : null;
}

export interface ReadAttachmentsOptions {
  /** Overrides ATTACHMENT_MAX_COUNT (tests). */
  readonly maxCount?: number;
  /** Overrides ATTACHMENT_MAX_BYTES (tests). */
  readonly maxBytes?: number;
  /** Receives one line per skipped file. */
  readonly log?: (message: string) => void;
}

/**
 * Reads the files the user picked, in order. Files of an unsupported type, empty files, files
 * larger than the size limit, unreadable files and files beyond the count limit are skipped
 * (and logged); the valid ones are returned.
 */
export async function readPickedAttachments(
  filePaths: readonly string[],
  options: ReadAttachmentsOptions = {},
): Promise<PickedAttachment[]> {
  const maxCount = options.maxCount ?? ATTACHMENT_MAX_COUNT;
  const maxBytes = options.maxBytes ?? ATTACHMENT_MAX_BYTES;
  const log = options.log ?? (() => undefined);
  const picked: PickedAttachment[] = [];

  for (const filePath of filePaths) {
    const name = path.basename(filePath);
    if (picked.length >= maxCount) {
      log(`Skipped "${name}": at most ${maxCount} files can be attached to one message.`);
      continue;
    }
    const mediaType = mediaTypeForFile(name);
    if (mediaType === null) {
      log(`Skipped "${name}": this file type cannot be attached.`);
      continue;
    }
    try {
      const stats = await stat(filePath);
      if (!stats.isFile()) {
        log(`Skipped "${name}": it is not a file.`);
        continue;
      }
      if (stats.size > maxBytes) {
        log(`Skipped "${name}": it is larger than the ${maxBytes}-byte attachment limit.`);
        continue;
      }
      const data = await readFile(filePath);
      // The file may have changed between stat and read.
      if (data.byteLength === 0) {
        log(`Skipped "${name}": the file is empty.`);
        continue;
      }
      if (data.byteLength > maxBytes) {
        log(`Skipped "${name}": it is larger than the ${maxBytes}-byte attachment limit.`);
        continue;
      }
      picked.push({
        name: name.slice(0, ATTACHMENT_NAME_MAX_LENGTH),
        mediaType,
        dataBase64: data.toString('base64'),
        sizeBytes: data.byteLength,
      });
    } catch (error) {
      log(`Skipped "${name}": ${errorMessage(error)}`);
    }
  }
  return picked;
}

export type AttachmentsCheck = { ok: true; attachments: PickedAttachment[] } | { ok: false; message: string };

const BASE64_PATTERN = /^[A-Za-z0-9+/]*={0,2}$/;

/** Decoded size of canonical (padded) base64 text, or null when the text is not valid base64. */
export function base64DecodedSize(text: string): number | null {
  if (text.length % 4 !== 0 || !BASE64_PATTERN.test(text)) return null;
  if (text.length === 0) return 0;
  const padding = text.endsWith('==') ? 2 : text.endsWith('=') ? 1 : 0;
  return (text.length / 4) * 3 - padding;
}

/**
 * Validates the attachments the renderer sends with a message: an array within the count limit
 * whose items have a name, an accepted media type and non-empty base64 data within the size
 * limit. The size is recomputed from the data; whatever size the renderer reported is ignored.
 */
export function checkAttachments(value: unknown): AttachmentsCheck {
  if (!Array.isArray(value)) return { ok: false, message: 'The attachments must be a list.' };
  if (value.length > ATTACHMENT_MAX_COUNT) {
    return { ok: false, message: `At most ${ATTACHMENT_MAX_COUNT} files can be attached to one message.` };
  }
  const attachments: PickedAttachment[] = [];
  for (const item of value as unknown[]) {
    if (typeof item !== 'object' || item === null) return { ok: false, message: 'An attachment is not valid.' };
    const { name, mediaType, dataBase64 } = item as Record<string, unknown>;
    if (typeof name !== 'string' || name.trim().length === 0 || name.length > ATTACHMENT_NAME_MAX_LENGTH) {
      return { ok: false, message: 'An attachment has no valid file name.' };
    }
    if (typeof mediaType !== 'string' || !ATTACHMENT_MEDIA_TYPES.has(mediaType)) {
      return { ok: false, message: `"${name}" cannot be attached: only images, PDF and text files are supported.` };
    }
    if (typeof dataBase64 !== 'string') return { ok: false, message: `"${name}" has no content.` };
    // Cheap bound first: base64 is 4 characters per 3 bytes.
    if (dataBase64.length > Math.ceil(ATTACHMENT_MAX_BYTES / 3) * 4) {
      return { ok: false, message: `"${name}" is larger than the attachment size limit.` };
    }
    const sizeBytes = base64DecodedSize(dataBase64);
    if (sizeBytes === null) return { ok: false, message: `"${name}" has damaged content.` };
    if (sizeBytes === 0) return { ok: false, message: `"${name}" is empty.` };
    if (sizeBytes > ATTACHMENT_MAX_BYTES) {
      return { ok: false, message: `"${name}" is larger than the attachment size limit.` };
    }
    attachments.push({ name, mediaType, dataBase64, sizeBytes });
  }
  return { ok: true, attachments };
}

/** What the timeline shows of an attachment (never its content). */
export function attachmentInfo(attachment: PickedAttachment): AttachmentInfo {
  return { name: attachment.name, mediaType: attachment.mediaType, sizeBytes: attachment.sizeBytes };
}
