/** "CAD activity": one row per tool call, reporting only what the CAD application confirmed. */
import { Check, ChevronRight, Minus, TriangleAlert, X } from 'lucide-react';
import { memo, useState } from 'react';
import { changeSummary } from '../lib/format';
import type { ActivityGroupItem, ActivityRow } from '../state/transcript';
import { Spinner, cx } from './ui';

interface ActivityGroupProps {
  item: ActivityGroupItem;
  /** Display name of the connected CAD, for "Working in <CAD>". */
  cadName: string;
  /** Call id of the newest running call in the whole transcript, or null. */
  newestRunningCallId: string | null;
}

function StatusIcon({ status }: { status: ActivityRow['status'] }) {
  switch (status) {
    case 'running':
      return <Spinner size={14} className="text-accent-soft" />;
    case 'done':
      return <Check size={14} aria-hidden className="shrink-0 text-success" />;
    case 'failed':
      return <X size={14} aria-hidden className="shrink-0 text-danger" />;
    default:
      return <Minus size={14} aria-hidden className="shrink-0 text-fg-faint" />;
  }
}

const STATUS_WORDS: Record<ActivityRow['status'], string> = {
  running: 'Running',
  done: 'Done',
  failed: 'Failed',
  stopped: 'Stopped before it finished',
};

function ActivityRowView({ row, cadName, newest }: { row: ActivityRow; cadName: string; newest: boolean }) {
  const [expanded, setExpanded] = useState(false);
  const changes = changeSummary(row);
  const settled = row.status === 'done' || row.status === 'failed';
  const hasDetails = settled || row.status === 'stopped';
  const working = row.status === 'running' && newest;
  const detailsId = `activity-details-${row.callId}`;

  return (
    <li data-testid="activity-row" data-status={row.status} data-tool={row.name} className="min-w-0">
      <button
        type="button"
        disabled={!hasDetails}
        aria-expanded={hasDetails ? expanded : undefined}
        aria-controls={hasDetails ? detailsId : undefined}
        onClick={() => setExpanded((value) => !value)}
        className={cx(
          'flex w-full items-start gap-2 rounded-md px-2 py-1 text-left transition-colors',
          hasDetails ? 'hover:bg-hover' : 'cursor-default',
        )}
      >
        <span className="mt-[3px] flex h-3.5 w-3.5 shrink-0 items-center justify-center">
          <StatusIcon status={row.status} />
        </span>
        <span className="min-w-0 flex-1">
          <span className="sr-only">{STATUS_WORDS[row.status]}: </span>
          {working ? (
            <>
              <span className="block text-fg">Working in {cadName}</span>
              <span className="block text-[12px] text-fg-dim wrap-anywhere">{row.summary}</span>
            </>
          ) : (
            <span
              className={cx(
                'block wrap-anywhere',
                row.status === 'stopped' ? 'text-fg-dim' : 'text-fg',
              )}
            >
              {row.summary}
            </span>
          )}
          {row.status === 'failed' && row.error !== null ? (
            <span className="block text-[12px] text-danger wrap-anywhere">{row.error}</span>
          ) : null}
        </span>
        {hasDetails ? (
          <ChevronRight
            size={13}
            aria-hidden
            className={cx('mt-[3px] shrink-0 text-fg-faint transition-transform', expanded && 'rotate-90')}
          />
        ) : null}
      </button>
      {hasDetails && expanded ? (
        <div
          id={detailsId}
          data-testid="activity-details"
          className="selectable mb-1 ml-[30px] mr-2 flex flex-col gap-1 border-l border-line pl-2.5 text-[12px] text-fg-dim"
        >
          <div className="text-fg-faint">{row.title}</div>
          {row.status === 'stopped' ? <div>Stopped before the CAD application confirmed a result.</div> : null}
          {settled ? <div>{changes ?? 'No objects were created, modified or deleted.'}</div> : null}
          {row.warnings.map((warning, index) => (
            <div key={`w-${index}`} className="flex items-start gap-1.5 text-warning">
              <TriangleAlert size={12} aria-hidden className="mt-[3px] shrink-0" />
              <span className="min-w-0 wrap-anywhere">{warning}</span>
            </div>
          ))}
          {row.errors.map((error, index) => (
            <div key={`e-${index}`} className="flex items-start gap-1.5 text-danger">
              <X size={12} aria-hidden className="mt-[3px] shrink-0" />
              <span className="min-w-0 wrap-anywhere">{error}</span>
            </div>
          ))}
        </div>
      ) : null}
    </li>
  );
}

/** A run of consecutive tool calls. */
export const ActivityGroup = memo(function ActivityGroup({ item, cadName, newestRunningCallId }: ActivityGroupProps) {
  return (
    <section data-testid="activity-group" aria-label="CAD activity" className="rounded-xl border border-line bg-card py-1.5">
      <h3 className="px-3 pb-1 pt-0.5 text-[10px] font-semibold uppercase tracking-[0.08em] text-fg-faint">CAD activity</h3>
      <ul className="m-0 flex list-none flex-col px-1">
        {item.rows.map((row) => (
          <ActivityRowView
            key={row.callId}
            row={row}
            cadName={cadName}
            newest={row.callId === newestRunningCallId}
          />
        ))}
      </ul>
    </section>
  );
});
