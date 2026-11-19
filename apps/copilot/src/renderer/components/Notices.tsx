/** Inline error notices, the subtle turn footer and the "Thinking" indicator. */
import { CircleAlert } from 'lucide-react';
import { memo } from 'react';
import { hasTokenUsage, modelLabel, stopReasonLabel, usageDetail, usageLabel } from '../lib/format';
import type { ErrorItem, TurnFooterItem } from '../state/transcript';

/** An 'error' event: a red notice in the flow of the conversation. */
export const ErrorNotice = memo(function ErrorNotice({ item }: { item: ErrorItem }) {
  return (
    <div
      data-testid="error-notice"
      role="alert"
      className="flex items-start gap-2 rounded-lg border border-danger/40 bg-danger/10 px-3 py-2 text-[12.5px]"
    >
      <CircleAlert size={14} aria-hidden className="mt-[3px] shrink-0 text-danger" />
      <div className="selectable min-w-0 wrap-anywhere">
        <div className="text-[#ffb4b0]">{item.message}</div>
        {item.retryable ? <div className="mt-0.5 text-fg-dim">This may be temporary. Send your message again to retry.</div> : null}
      </div>
    </div>
  );
});

/** How a turn ended: 'Stopped' and/or the model name with its token usage. */
export const TurnFooter = memo(function TurnFooter({ item }: { item: TurnFooterItem }) {
  const reason = stopReasonLabel(item.stopReason, item.stopped);
  const parts: { text: string; title?: string }[] = [];
  if (reason !== null) parts.push({ text: reason });
  if (item.model !== null && item.model.length > 0) parts.push({ text: modelLabel(item.model), title: item.model });
  // A turn that used no tokens (stopped before the model answered, or the scripted test model) has no usage worth showing.
  if (item.usage !== null && hasTokenUsage(item.usage)) parts.push({ text: usageLabel(item.usage), title: usageDetail(item.usage) });
  if (parts.length === 0) return null;
  return (
    <div
      data-testid="turn-footer"
      data-stopped={item.stopped ? 'true' : 'false'}
      className="-mt-1 flex flex-wrap items-center gap-x-1.5 text-[11px] text-fg-faint"
    >
      {parts.map((part, index) => (
        <span key={part.text} title={part.title} className={index === 0 && reason !== null ? 'text-fg-dim' : undefined}>
          {index > 0 ? <span aria-hidden className="mr-1.5">&middot;</span> : null}
          {part.text}
        </span>
      ))}
    </div>
  );
});

/** Shown while the copilot is busy and nothing else in the transcript shows progress. */
export function ThinkingIndicator() {
  return (
    <div data-testid="thinking" role="status" className="flex h-5 items-center gap-1.5 text-fg-dim">
      <span aria-hidden className="flex items-center gap-1">
        <span className="animate-dot h-1.5 w-1.5 rounded-full bg-fg-dim" />
        <span className="animate-dot h-1.5 w-1.5 rounded-full bg-fg-dim [animation-delay:0.15s]" />
        <span className="animate-dot h-1.5 w-1.5 rounded-full bg-fg-dim [animation-delay:0.3s]" />
      </span>
      <span className="text-[12px]">Thinking</span>
    </div>
  );
}
