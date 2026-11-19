/** Small pure formatting helpers for the copilot UI (unit-tested in Node). */
import type { TurnUsage } from '@cadstudio/copilot-agent';
import { MODEL_CHOICES } from '../../shared/bridge';
import type { ConnectionState } from '../../shared/bridge';

/** '1 object' / '3 objects'. */
export function pluralize(count: number, singular: string, plural = `${singular}s`): string {
  return `${count} ${count === 1 ? singular : plural}`;
}

/** Compact token counts: 850 -> '850', 12 340 -> '12.3k', 1 200 000 -> '1.2M'. */
export function formatTokenCount(count: number): string {
  if (!Number.isFinite(count) || count <= 0) return '0';
  const value = Math.round(count);
  if (value < 1000) return String(value);
  if (value < 999_500) {
    const thousands = value / 1000;
    return `${thousands >= 100 ? Math.round(thousands) : trimDecimal(thousands)}k`;
  }
  return `${trimDecimal(value / 1_000_000)}M`;
}

function trimDecimal(value: number): string {
  const fixed = value.toFixed(1);
  return fixed.endsWith('.0') ? fixed.slice(0, -2) : fixed;
}

/** File sizes: 512 -> '512 B', 2048 -> '2 KB', 1 572 864 -> '1.5 MB'. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  if (bytes < 1024 * 1024) return `${trimDecimal(bytes / 1024)} KB`;
  return `${trimDecimal(bytes / (1024 * 1024))} MB`;
}

/** The display name of a model id; unknown ids (for example dated API ids) are shown as they are. */
export function modelLabel(model: string): string {
  // The scripted test model reports itself as 'scripted' (SCRIPTED_MODEL_NAME of the agent package,
  // which the renderer must not bundle).
  if (model === 'scripted') return 'Test model';
  const exact = MODEL_CHOICES.find((choice) => choice.id === model);
  if (exact) return exact.label;
  const dated = MODEL_CHOICES.find((choice) => model.startsWith(`${choice.id}-`));
  return dated ? dated.label : model;
}

/** Everything the model read for a turn: fresh input plus cache reads and cache writes. */
export function totalInputTokens(usage: TurnUsage): number {
  return safeCount(usage.inputTokens) + safeCount(usage.cacheReadTokens) + safeCount(usage.cacheCreationTokens);
}

function safeCount(value: number): number {
  return Number.isFinite(value) && value > 0 ? value : 0;
}

/** False for a turn that used no tokens (stopped before the model answered, or the scripted test model). */
export function hasTokenUsage(usage: TurnUsage): boolean {
  return totalInputTokens(usage) + safeCount(usage.outputTokens) > 0;
}

/** '12.3k in / 850 out' for the turn footer. */
export function usageLabel(usage: TurnUsage): string {
  return `${formatTokenCount(totalInputTokens(usage))} in / ${formatTokenCount(safeCount(usage.outputTokens))} out`;
}

/** Tooltip with the full token breakdown of a turn. */
export function usageDetail(usage: TurnUsage): string {
  const parts = [
    `Input ${safeCount(usage.inputTokens).toLocaleString('en-US')}`,
    `cache read ${safeCount(usage.cacheReadTokens).toLocaleString('en-US')}`,
    `cache write ${safeCount(usage.cacheCreationTokens).toLocaleString('en-US')}`,
    `output ${safeCount(usage.outputTokens).toLocaleString('en-US')}`,
  ];
  return `${parts.join(', ')} tokens`;
}

/**
 * What the turn footer says about how a turn ended, or null when it ended normally.
 * `stopped` is true when the user pressed Stop.
 */
export function stopReasonLabel(stopReason: string | null, stopped: boolean): string | null {
  if (stopped || stopReason === 'stopped') return 'Stopped';
  switch (stopReason) {
    case null:
    case 'end_turn':
    case 'stop_sequence':
    case 'tool_use':
      return null;
    case 'max_tokens':
      return 'Reached the output limit';
    case 'refusal':
      return 'The model declined to continue';
    case 'pause_turn':
      return 'Paused';
    case 'error':
      return 'Ended with an error';
    default:
      return null;
  }
}

/** 'Created 2 objects, modified 1 object', or null when the call changed nothing. */
export function changeSummary(counts: { created: number; modified: number; deleted: number }): string | null {
  const parts: string[] = [];
  if (counts.created > 0) parts.push(`created ${pluralize(counts.created, 'object')}`);
  if (counts.modified > 0) parts.push(`modified ${pluralize(counts.modified, 'object')}`);
  if (counts.deleted > 0) parts.push(`deleted ${pluralize(counts.deleted, 'object')}`);
  if (parts.length === 0) return null;
  const sentence = parts.join(', ');
  return sentence.charAt(0).toUpperCase() + sentence.slice(1);
}

/** The composer's attachment counter: '1 input', '2 inputs'. */
export function inputsLabel(count: number): string {
  return pluralize(count, 'input');
}

export type AttachmentKind = 'image' | 'pdf' | 'text' | 'file';

/** Which icon an attachment chip shows. */
export function attachmentKind(mediaType: string): AttachmentKind {
  const type = mediaType.toLowerCase();
  if (type.startsWith('image/')) return 'image';
  if (type === 'application/pdf') return 'pdf';
  if (type.startsWith('text/') || type === 'application/json') return 'text';
  return 'file';
}

export interface ConnectionLabel {
  /** connected: green dot; busy: spinner; idle: grey dot; error: red dot. */
  tone: 'connected' | 'busy' | 'idle' | 'error';
  /** The CAD's name when connected, otherwise the state in words. */
  primary: string;
  /** The drawing's name when connected and a drawing is open. */
  secondary: string | null;
  /** Full sentence for tooltips and screen readers. */
  description: string;
}

/** What the header's connection chip shows for a connection state. */
export function connectionLabel(connection: ConnectionState, connectorName?: string): ConnectionLabel {
  switch (connection.status) {
    case 'connected': {
      const cad = connection.connector.displayName;
      const drawing = connection.document?.name ?? null;
      return {
        tone: 'connected',
        primary: cad,
        secondary: drawing,
        description: drawing ? `Connected to ${cad}, drawing ${drawing}` : `Connected to ${cad}, no drawing open`,
      };
    }
    case 'connecting': {
      const target = connectorName ?? 'the CAD application';
      return { tone: 'busy', primary: 'Connecting...', secondary: null, description: `Connecting to ${target}` };
    }
    case 'error':
      return {
        tone: 'error',
        primary: 'Not connected',
        secondary: null,
        description: `Not connected: ${connection.message}`,
      };
    default:
      return { tone: 'idle', primary: 'Not connected', secondary: null, description: 'Not connected to a CAD application' };
  }
}

/** The CAD's display name when connected, otherwise a neutral fallback for "Working in ...". */
export function cadDisplayName(connection: ConnectionState): string {
  return connection.status === 'connected' ? connection.connector.displayName : 'the CAD application';
}
