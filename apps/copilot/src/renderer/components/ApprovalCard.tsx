/** Approval of one drawing change: Approve / Reject / Always allow, collapsing once resolved. */
import { Ban, Check, Minus, ShieldAlert } from 'lucide-react';
import { memo, useEffect, useState } from 'react';
import type { ApprovalDecision, ApprovalItem } from '../state/transcript';
import { Button, Card, cx } from './ui';

/** How long the buttons stay disabled while the main process confirms the decision. */
const DECISION_ACK_TIMEOUT_MS = 4000;

interface ApprovalCardProps {
  item: ApprovalItem;
  onResolve: (requestId: string, decision: ApprovalDecision) => void;
}

function RiskTag({ risk }: { risk: ApprovalItem['risk'] }) {
  const destructive = risk === 'destructive';
  return (
    <span
      data-testid="approval-risk"
      data-risk={risk}
      className={cx(
        'inline-flex h-[18px] shrink-0 items-center rounded px-1.5 text-[10px] font-semibold uppercase tracking-[0.06em]',
        destructive ? 'bg-danger/15 text-danger' : 'bg-warning/15 text-warning',
      )}
    >
      {destructive ? 'Destructive' : 'Changes drawing'}
    </span>
  );
}

const RESOLVED_TEXT: Record<ApprovalDecision, string> = {
  approve: 'Approved',
  'approve-all': 'Always allowed',
  reject: 'Rejected',
};

/**
 * Asks before a tool changes the drawing. "Always allow" (approve everything for the rest of the
 * session) is never offered for destructive tools.
 */
export const ApprovalCard = memo(function ApprovalCard({ item, onResolve }: ApprovalCardProps) {
  const [sent, setSent] = useState<ApprovalDecision | null>(null);

  useEffect(() => {
    if (sent === null) return undefined;
    const timer = window.setTimeout(() => setSent(null), DECISION_ACK_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [sent]);

  if (item.decision !== null || item.cancelled) {
    const state = item.decision === null ? 'cancelled' : item.decision === 'reject' ? 'rejected' : 'approved';
    return (
      <div
        data-testid="approval-card"
        data-state={state}
        className="flex items-start gap-2 rounded-lg border border-line bg-card px-3 py-1.5 text-[12px]"
      >
        <span className="mt-[3px] flex h-3.5 w-3.5 shrink-0 items-center justify-center">
          {item.decision === null ? (
            <Minus size={13} aria-hidden className="text-fg-faint" />
          ) : item.decision === 'reject' ? (
            <Ban size={13} aria-hidden className="text-danger" />
          ) : (
            <Check size={13} aria-hidden className="text-success" />
          )}
        </span>
        <span className="min-w-0 text-fg-dim wrap-anywhere">
          <span className={cx('font-medium', item.decision === 'reject' ? 'text-danger' : 'text-fg')}>
            {item.decision === null ? 'Not decided' : RESOLVED_TEXT[item.decision]}
          </span>
          {': '}
          {item.summary}
        </span>
      </div>
    );
  }

  const decide = (decision: ApprovalDecision): void => {
    if (sent !== null) return;
    setSent(decision);
    onResolve(item.requestId, decision);
  };
  const destructive = item.risk === 'destructive';

  return (
    <Card
      data-testid="approval-card"
      data-state="pending"
      role="group"
      aria-label={`Approval needed: ${item.title}`}
      className={cx('px-3 py-3', destructive ? 'border-danger/50' : 'border-line-strong')}
    >
      <div className="flex items-center gap-2">
        <ShieldAlert size={14} aria-hidden className={cx('shrink-0', destructive ? 'text-danger' : 'text-warning')} />
        <span className="min-w-0 flex-1 truncate font-medium text-fg">Approval needed</span>
        <RiskTag risk={item.risk} />
      </div>
      <div className="selectable mt-1.5 text-fg wrap-anywhere">{item.summary}</div>
      {item.title !== item.summary ? (
        <div className="mt-0.5 text-[12px] text-fg-faint wrap-anywhere">{item.title}</div>
      ) : null}
      <div className="mt-2.5 flex flex-wrap items-center gap-1.5">
        <Button
          variant={destructive ? 'danger' : 'primary'}
          data-testid="approval-approve"
          disabled={sent !== null}
          onClick={() => decide('approve')}
        >
          Approve
        </Button>
        <Button variant="secondary" data-testid="approval-reject" disabled={sent !== null} onClick={() => decide('reject')}>
          Reject
        </Button>
        {destructive ? null : (
          <Button
            variant="ghost"
            data-testid="approval-always"
            disabled={sent !== null}
            title="Approve this and every later change in this conversation (deleting and saving still ask)"
            onClick={() => decide('approve-all')}
          >
            Always allow
          </Button>
        )}
      </div>
    </Card>
  );
});
