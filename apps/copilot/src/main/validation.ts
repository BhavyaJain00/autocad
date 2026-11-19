/**
 * Validation of the arguments the renderer sends over IPC. The renderer is treated as untrusted:
 * every value is checked for type, shape and size before the main process acts on it.
 * Pure; no Electron imports.
 */
import type { QuestionAnswer } from '@cadstudio/copilot-agent';
import type { PickedAttachment } from '../shared/bridge';
import { checkAttachments } from './attachments';
import { MESSAGE_MAX_LENGTH } from './constants';

export type ApprovalDecision = 'approve' | 'reject' | 'approve-all';

/** Longest request / question id accepted. */
export const ID_MAX_LENGTH = 200;
/** Most answers accepted for one question request. */
export const ANSWERS_MAX_COUNT = 50;
/** Longest single answer accepted (characters). */
export const ANSWER_MAX_LENGTH = 10_000;
/** Longest URL accepted by openExternal. */
export const URL_MAX_LENGTH = 2_048;

export type MessageCheck =
  | { ok: true; text: string; attachments: PickedAttachment[] }
  | { ok: false; message: string };

/**
 * A user message: text of at most MESSAGE_MAX_LENGTH characters that is not blank unless files
 * are attached, plus valid attachments. The returned text is trimmed.
 */
export function checkMessage(text: unknown, attachments: unknown): MessageCheck {
  if (typeof text !== 'string') return { ok: false, message: 'The message must be text.' };
  if (text.length > MESSAGE_MAX_LENGTH) {
    return {
      ok: false,
      message: `The message is too long (the limit is ${MESSAGE_MAX_LENGTH.toLocaleString('en-US')} characters).`,
    };
  }
  const checked = checkAttachments(attachments ?? []);
  if (!checked.ok) return checked;
  const trimmed = text.trim();
  if (trimmed.length === 0 && checked.attachments.length === 0) {
    return { ok: false, message: 'Type a message or attach a file first.' };
  }
  return { ok: true, text: trimmed, attachments: checked.attachments };
}

/** A request id issued by the session: a non-empty string of bounded length, or null. */
export function checkRequestId(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 && value.length <= ID_MAX_LENGTH ? value : null;
}

/** Answers to a question card: `{ questionId, answer }` items of bounded size, or null. */
export function checkAnswers(value: unknown): QuestionAnswer[] | null {
  if (!Array.isArray(value) || value.length > ANSWERS_MAX_COUNT) return null;
  const answers: QuestionAnswer[] = [];
  for (const item of value as unknown[]) {
    if (typeof item !== 'object' || item === null) return null;
    const { questionId, answer } = item as Record<string, unknown>;
    if (checkRequestId(questionId) === null) return null;
    if (typeof answer !== 'string' || answer.length > ANSWER_MAX_LENGTH) return null;
    answers.push({ questionId: questionId as string, answer });
  }
  return answers;
}

/** An approval decision, or null. */
export function checkDecision(value: unknown): ApprovalDecision | null {
  return value === 'approve' || value === 'reject' || value === 'approve-all' ? value : null;
}

/** A connector advert id ('<cad>-<pid>'): letters, digits, '.', '_' and '-' only, or null. */
export function checkConnectorId(value: unknown): string | null {
  return typeof value === 'string' && /^[A-Za-z0-9._-]{1,128}$/.test(value) ? value : null;
}

/**
 * The normalised form of a URL that may be opened in the default browser: an absolute https URL
 * with a host and no embedded credentials. Anything else is null.
 */
export function checkExternalUrl(value: unknown): string | null {
  if (typeof value !== 'string' || value.length === 0 || value.length > URL_MAX_LENGTH) return null;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  if (url.protocol !== 'https:' || url.hostname.length === 0) return null;
  if (url.username.length > 0 || url.password.length > 0) return null;
  return url.href;
}
