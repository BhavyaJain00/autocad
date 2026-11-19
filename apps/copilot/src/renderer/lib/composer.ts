/** Pure composer rules: when a message can be sent, how attachments merge, which key sends. */
import { ATTACHMENT_MAX_BYTES, ATTACHMENT_MAX_COUNT } from '../../shared/bridge';
import type { PickedAttachment } from '../../shared/bridge';
import { formatBytes } from './format';

export interface SendContext {
  text: string;
  attachmentCount: number;
  /** A turn is running (the Stop button is shown). */
  busy: boolean;
  /** A send request is on its way to the main process. */
  sending: boolean;
  hasApiKey: boolean;
  testModel: boolean;
}

export interface SendBlock {
  /** Why sending is disabled, in words. */
  reason: string;
  /** True when the reason deserves a visible hint (an empty draft does not). */
  prominent: boolean;
}

/** Why the Send button is disabled, or null when the draft can be sent. */
export function sendBlock(context: SendContext): SendBlock | null {
  if (!context.hasApiKey && !context.testModel) {
    return { reason: 'Add your Anthropic API key to start chatting.', prominent: true };
  }
  if (context.busy) {
    return { reason: 'The copilot is working. Press Stop to interrupt it.', prominent: false };
  }
  if (context.sending) {
    return { reason: 'Sending...', prominent: false };
  }
  if (context.text.trim().length === 0 && context.attachmentCount === 0) {
    return { reason: 'Type a message to send.', prominent: false };
  }
  return null;
}

export interface KeyLike {
  key: string;
  shiftKey: boolean;
  ctrlKey: boolean;
  altKey: boolean;
  metaKey: boolean;
  /** True while an input method editor is composing (Enter then confirms the composition). */
  isComposing: boolean;
}

/** Enter sends; Shift+Enter (or any other modifier) and IME confirmation do not. */
export function isSendKey(event: KeyLike): boolean {
  return (
    event.key === 'Enter' &&
    !event.shiftKey &&
    !event.ctrlKey &&
    !event.altKey &&
    !event.metaKey &&
    !event.isComposing
  );
}

export interface AttachmentMerge {
  attachments: PickedAttachment[];
  /** A sentence about files that were left out, or null when everything was added. */
  notice: string | null;
}

function sameFile(a: PickedAttachment, b: PickedAttachment): boolean {
  return a.name === b.name && a.sizeBytes === b.sizeBytes && a.mediaType === b.mediaType;
}

/**
 * Adds newly picked files to the composer's attachments: skips files that are already attached,
 * files over the size limit and anything beyond the count limit (the main process enforces the
 * same limits; this keeps the UI honest if it ever returns more).
 */
export function mergeAttachments(
  current: readonly PickedAttachment[],
  picked: readonly PickedAttachment[],
  maxCount: number = ATTACHMENT_MAX_COUNT,
  maxBytes: number = ATTACHMENT_MAX_BYTES,
): AttachmentMerge {
  const attachments = current.slice();
  let tooLarge = 0;
  let overLimit = 0;
  for (const file of picked) {
    if (attachments.some((existing) => sameFile(existing, file))) continue;
    if (file.sizeBytes > maxBytes) {
      tooLarge += 1;
      continue;
    }
    if (attachments.length >= maxCount) {
      overLimit += 1;
      continue;
    }
    attachments.push(file);
  }
  const notes: string[] = [];
  if (tooLarge > 0) {
    notes.push(
      `${tooLarge === 1 ? '1 file is' : `${tooLarge} files are`} larger than ${formatBytes(maxBytes)} and ${tooLarge === 1 ? 'was' : 'were'} skipped.`,
    );
  }
  if (overLimit > 0) {
    notes.push(`Only ${maxCount} attachments fit in one message; ${overLimit} ${overLimit === 1 ? 'was' : 'were'} left out.`);
  }
  return { attachments, notice: notes.length > 0 ? notes.join(' ') : null };
}

/** Removes the attachment at `index`; an out-of-range index changes nothing. */
export function removeAttachment(current: readonly PickedAttachment[], index: number): PickedAttachment[] {
  if (!Number.isInteger(index) || index < 0 || index >= current.length) return current.slice();
  return current.filter((_, i) => i !== index);
}

/** Example prompts offered on the welcome card (imperial; see {@link examplePromptsFor}). */
export const EXAMPLE_PROMPTS: readonly string[] = [
  'Draw a 12 x 14 ft room',
  'What is in this drawing?',
  'Move the selected objects 3 ft to the right',
  'Create a layer for furniture and draw a 6 x 3 ft table',
];

/** The same examples in metric sizes, for drawings in mm, cm or m. */
export const METRIC_EXAMPLE_PROMPTS: readonly string[] = [
  'Draw a 4 x 5 m room',
  'What is in this drawing?',
  'Move the selected objects 1 m to the right',
  'Create a layer for furniture and draw a 1.8 x 0.9 m table',
];

/**
 * Example prompts that match the drawing's unit system: metric for mm / cm / m drawings,
 * imperial for in / ft drawings and when no drawing is known.
 */
export function examplePromptsFor(unit: string | null | undefined): readonly string[] {
  return unit === 'mm' || unit === 'cm' || unit === 'm' ? METRIC_EXAMPLE_PROMPTS : EXAMPLE_PROMPTS;
}
