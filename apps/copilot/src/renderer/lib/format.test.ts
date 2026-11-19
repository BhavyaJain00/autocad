import { describe, it, expect } from 'vitest';
import type { TurnUsage } from '@cadstudio/copilot-agent';
import type { ConnectionState, ConnectorSummary } from '../../shared/bridge';
import {
  attachmentKind,
  cadDisplayName,
  changeSummary,
  connectionLabel,
  formatBytes,
  formatTokenCount,
  hasTokenUsage,
  inputsLabel,
  modelLabel,
  pluralize,
  stopReasonLabel,
  totalInputTokens,
  usageDetail,
  usageLabel,
} from './format';

const CONNECTOR: ConnectorSummary = { id: 'cadstudio-1', cad: 'cadstudio', displayName: 'CAD Studio', cadVersion: '0.1.0', pid: 1 };

function connected(documentName: string | null): ConnectionState {
  return {
    status: 'connected',
    connector: CONNECTOR,
    document:
      documentName === null
        ? null
        : { id: 'd', name: documentName, path: null, unit: 'mm', revision: 0, entityCount: 0 },
    supportedTools: [],
    docked: false,
  };
}

describe('pluralize', () => {
  it('uses the singular only for exactly one', () => {
    expect(pluralize(1, 'object')).toBe('1 object');
    expect(pluralize(0, 'object')).toBe('0 objects');
    expect(pluralize(3, 'object')).toBe('3 objects');
    expect(pluralize(2, 'entity', 'entities')).toBe('2 entities');
  });
});

describe('formatTokenCount', () => {
  it('formats small, thousand and million counts', () => {
    expect(formatTokenCount(0)).toBe('0');
    expect(formatTokenCount(850)).toBe('850');
    expect(formatTokenCount(999)).toBe('999');
    expect(formatTokenCount(1000)).toBe('1k');
    expect(formatTokenCount(12_340)).toBe('12.3k');
    expect(formatTokenCount(125_600)).toBe('126k');
    expect(formatTokenCount(99_960)).toBe('100k');
    expect(formatTokenCount(999_499)).toBe('999k');
    expect(formatTokenCount(999_500)).toBe('1M');
    expect(formatTokenCount(1_200_000)).toBe('1.2M');
  });

  it('treats invalid counts as zero', () => {
    expect(formatTokenCount(-5)).toBe('0');
    expect(formatTokenCount(Number.NaN)).toBe('0');
    expect(formatTokenCount(Number.POSITIVE_INFINITY)).toBe('0');
  });
});

describe('formatBytes', () => {
  it('formats bytes, kilobytes and megabytes', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(2048)).toBe('2 KB');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(1_572_864)).toBe('1.5 MB');
    expect(formatBytes(10 * 1024 * 1024)).toBe('10 MB');
    expect(formatBytes(Number.NaN)).toBe('0 B');
  });
});

describe('modelLabel', () => {
  it('names known models, including dated ids, and passes unknown ids through', () => {
    expect(modelLabel('claude-opus-5-5')).toBe('Claude Opus 5.5');
    expect(modelLabel('claude-haiku-5-5')).toBe('Claude Haiku 5.5');
    expect(modelLabel('claude-sonnet-5-5-20260801')).toBe('Claude Sonnet 5.5');
    expect(modelLabel('scripted-test-model')).toBe('scripted-test-model');
    expect(modelLabel('scripted')).toBe('Test model');
  });
});

describe('usage', () => {
  const usage: TurnUsage = { inputTokens: 1200, outputTokens: 850, cacheReadTokens: 11_000, cacheCreationTokens: 140 };

  it('adds cache reads and writes to the input total', () => {
    expect(totalInputTokens(usage)).toBe(12_340);
    expect(usageLabel(usage)).toBe('12.3k in / 850 out');
  });

  it('spells out the breakdown', () => {
    expect(usageDetail(usage)).toBe('Input 1,200, cache read 11,000, cache write 140, output 850 tokens');
  });

  it('survives missing numbers', () => {
    const broken = { inputTokens: Number.NaN, outputTokens: -1, cacheReadTokens: 10, cacheCreationTokens: 0 };
    expect(usageLabel(broken)).toBe('10 in / 0 out');
  });
});

describe('stopReasonLabel', () => {
  it('says nothing for a normal end', () => {
    expect(stopReasonLabel('end_turn', false)).toBeNull();
    expect(stopReasonLabel(null, false)).toBeNull();
    expect(stopReasonLabel('something_new', false)).toBeNull();
  });

  it('names the unusual endings', () => {
    expect(stopReasonLabel(null, true)).toBe('Stopped');
    expect(stopReasonLabel('stopped', false)).toBe('Stopped');
    expect(stopReasonLabel('end_turn', true)).toBe('Stopped');
    expect(stopReasonLabel('max_tokens', false)).toBe('Reached the output limit');
    expect(stopReasonLabel('refusal', false)).toBe('The model declined to continue');
    expect(stopReasonLabel('error', false)).toBe('Ended with an error');
  });
});

describe('changeSummary', () => {
  it('lists only the kinds of change that happened', () => {
    expect(changeSummary({ created: 2, modified: 1, deleted: 0 })).toBe('Created 2 objects, modified 1 object');
    expect(changeSummary({ created: 0, modified: 0, deleted: 3 })).toBe('Deleted 3 objects');
    expect(changeSummary({ created: 1, modified: 0, deleted: 0 })).toBe('Created 1 object');
    expect(changeSummary({ created: 0, modified: 0, deleted: 0 })).toBeNull();
  });
});

describe('attachments', () => {
  it('counts inputs', () => {
    expect(inputsLabel(1)).toBe('1 input');
    expect(inputsLabel(2)).toBe('2 inputs');
  });

  it('classifies media types', () => {
    expect(attachmentKind('image/png')).toBe('image');
    expect(attachmentKind('IMAGE/JPEG')).toBe('image');
    expect(attachmentKind('application/pdf')).toBe('pdf');
    expect(attachmentKind('text/plain')).toBe('text');
    expect(attachmentKind('application/json')).toBe('text');
    expect(attachmentKind('application/octet-stream')).toBe('file');
  });
});

describe('connectionLabel', () => {
  it('shows the CAD and the drawing when connected', () => {
    const label = connectionLabel(connected('Drawing1'));
    expect(label.tone).toBe('connected');
    expect(label.primary).toBe('CAD Studio');
    expect(label.secondary).toBe('Drawing1');
    expect(label.description).toBe('Connected to CAD Studio, drawing Drawing1');
  });

  it('says when no drawing is open', () => {
    const label = connectionLabel(connected(null));
    expect(label.secondary).toBeNull();
    expect(label.description).toBe('Connected to CAD Studio, no drawing open');
  });

  it('covers the other states', () => {
    expect(connectionLabel({ status: 'disconnected' })).toMatchObject({ tone: 'idle', primary: 'Not connected', secondary: null });
    expect(connectionLabel({ status: 'connecting', connectorId: 'x' }, 'AutoCAD 2026')).toMatchObject({
      tone: 'busy',
      primary: 'Connecting...',
      description: 'Connecting to AutoCAD 2026',
    });
    expect(connectionLabel({ status: 'connecting', connectorId: 'x' }).description).toBe('Connecting to the CAD application');
    expect(connectionLabel({ status: 'error', message: 'Pipe closed' })).toMatchObject({
      tone: 'error',
      primary: 'Not connected',
      description: 'Not connected: Pipe closed',
    });
  });

  it('names the CAD for "Working in ..."', () => {
    expect(cadDisplayName(connected('Drawing1'))).toBe('CAD Studio');
    expect(cadDisplayName({ status: 'disconnected' })).toBe('the CAD application');
  });
});

describe('hasTokenUsage', () => {
  const zero: TurnUsage = { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 };
  it('is false for a turn without tokens and true as soon as any count is positive', () => {
    expect(hasTokenUsage(zero)).toBe(false);
    expect(hasTokenUsage({ ...zero, outputTokens: 1 })).toBe(true);
    expect(hasTokenUsage({ ...zero, cacheReadTokens: 5 })).toBe(true);
  });
  it('ignores negative and non-finite counts', () => {
    expect(hasTokenUsage({ ...zero, inputTokens: -3, outputTokens: Number.NaN })).toBe(false);
  });
});
