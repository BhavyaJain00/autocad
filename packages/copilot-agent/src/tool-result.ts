// Turning tool outcomes into the text of `tool_result` blocks: compact JSON, capped in size.
import type { ToolResult } from '@cadstudio/cad-protocol';

/** Largest tool result text sent to the model (characters; about 60 KB of JSON). */
export const MAX_TOOL_RESULT_CHARS = 60_000;

/** Tool result text for a call the user stopped before it ran. */
export const CANCELLED_MESSAGE = 'Cancelled by the user.';
/** Tool result text for a call the user stopped while the CAD was already running it. */
export const CANCELLED_RUNNING_MESSAGE =
  'Cancelled by the user. The CAD may already have applied this call; inspect the drawing before relying on it or repeating it.';
/** Tool result text for a call the user rejected in the approval card. */
export const DECLINED_MESSAGE = 'The user declined this action.';

const MAX_STRING_CHARS = 2_000;
const MAX_REPORTED_ARRAYS = 12;

interface CutArray {
  path: string;
  total: number;
  kept: number;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function longestArray(value: unknown): number {
  if (Array.isArray(value)) {
    let longest = value.length;
    for (const item of value) longest = Math.max(longest, longestArray(item));
    return longest;
  }
  if (isPlainObject(value)) {
    let longest = 0;
    for (const child of Object.values(value)) longest = Math.max(longest, longestArray(child));
    return longest;
  }
  return 0;
}

/** Copies `value`, keeping at most `limit` items of every array and `maxString` characters of every string. */
function prune(value: unknown, limit: number, maxString: number, path: string, cuts: CutArray[]): unknown {
  if (typeof value === 'string') {
    if (value.length <= maxString) return value;
    return `${value.slice(0, maxString)}... [${value.length - maxString} more characters cut]`;
  }
  if (Array.isArray(value)) {
    const kept = value.length > limit ? value.slice(0, limit) : value;
    if (value.length > limit) cuts.push({ path, total: value.length, kept: limit });
    return kept.map((item, index) => prune(item, limit, maxString, `${path}[${index}]`, cuts));
  }
  if (isPlainObject(value)) {
    const out: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(value)) {
      out[key] = prune(child, limit, maxString, path.length === 0 ? key : `${path}.${key}`, cuts);
    }
    return out;
  }
  return value;
}

function truncationNote(cuts: readonly CutArray[], stringsCut: boolean): Record<string, unknown> {
  const listed = cuts.slice(0, MAX_REPORTED_ARRAYS).map((cut) => `${cut.path}: ${cut.kept} of ${cut.total}`);
  if (cuts.length > MAX_REPORTED_ARRAYS) listed.push(`... and ${cuts.length - MAX_REPORTED_ARRAYS} more lists`);
  return {
    note:
      'This result was too large to return in full, so long lists were cut short' +
      (stringsCut ? ' and long texts were shortened' : '') +
      '. Only the first items of each list are shown. Ask for less at once (a filter, specific ids, a smaller limit) to see the rest.',
    lists: listed,
  };
}

function withNote(pruned: unknown, cuts: readonly CutArray[], stringsCut: boolean): string {
  const body = isPlainObject(pruned) ? pruned : { value: pruned };
  return JSON.stringify({ ...body, truncated: truncationNote(cuts, stringsCut) });
}

/**
 * Compact JSON for any JSON-compatible value, at most `maxChars` long. When the value is larger,
 * every array is cut to the same maximum length (the largest that fits) and a `truncated` entry
 * says which lists were cut and how many items they really have; very long strings are shortened
 * only when cutting lists alone is not enough.
 */
export function serializeForModel(value: unknown, maxChars: number = MAX_TOOL_RESULT_CHARS): string {
  const full = JSON.stringify(value) ?? 'null';
  if (full.length <= maxChars) return full;

  for (const maxString of [Number.POSITIVE_INFINITY, MAX_STRING_CHARS, 200]) {
    const fits = (limit: number): string | null => {
      const cuts: CutArray[] = [];
      const pruned = prune(value, limit, maxString, '', cuts);
      const text = withNote(pruned, cuts, Number.isFinite(maxString));
      return text.length <= maxChars ? text : null;
    };
    if (fits(0) === null) continue;
    // Binary search for the largest per-array length that still fits.
    let low = 0;
    let high = longestArray(value);
    let best = fits(0);
    while (low < high) {
      const mid = Math.ceil((low + high) / 2);
      const text = fits(mid);
      if (text === null) {
        high = mid - 1;
      } else {
        best = text;
        low = mid;
      }
    }
    if (best !== null) return best;
  }

  // Nothing structured fits (for example one enormous object without lists): say so honestly.
  const envelope: Record<string, unknown> = {};
  if (isPlainObject(value)) {
    if (typeof value.success === 'boolean') envelope.success = value.success;
    if (typeof value.revision === 'number') envelope.revision = value.revision;
  }
  envelope.truncated = {
    note: `This result was too large to return (${full.length} characters of JSON). Ask for less at once (a filter, specific ids, a smaller limit).`,
  };
  return JSON.stringify(envelope);
}

/** The `tool_result` text for a ToolResult: its compact JSON, capped at about 60 KB. */
export function serializeToolResult(result: ToolResult, maxChars: number = MAX_TOOL_RESULT_CHARS): string {
  return serializeForModel(result, maxChars);
}

/** The `tool_result` text for a call that did not produce a ToolResult. */
export function errorResultText(message: string, problems: readonly string[] = []): string {
  if (problems.length === 0) return message;
  return `${message}\n${problems.map((problem) => `- ${problem}`).join('\n')}`;
}
