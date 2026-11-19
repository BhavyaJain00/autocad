/** User and assistant messages, plus the attachment chip both the transcript and the composer use. */
import { FileText, Image as ImageIcon, Paperclip, X } from 'lucide-react';
import { memo } from 'react';
import type { AttachmentInfo } from '../../shared/bridge';
import { attachmentKind, formatBytes } from '../lib/format';
import type { AssistantMessageItem, UserMessageItem } from '../state/transcript';
import { Markdown } from './Markdown';
import { cx } from './ui';

function AttachmentIcon({ mediaType }: { mediaType: string }) {
  const kind = attachmentKind(mediaType);
  if (kind === 'image') return <ImageIcon size={12} aria-hidden className="shrink-0 text-fg-dim" />;
  if (kind === 'pdf' || kind === 'text') return <FileText size={12} aria-hidden className="shrink-0 text-fg-dim" />;
  return <Paperclip size={12} aria-hidden className="shrink-0 text-fg-dim" />;
}

interface AttachmentChipProps {
  attachment: AttachmentInfo;
  /** When given, the chip shows a remove button. */
  onRemove?: () => void;
  testId: string;
}

/** A file attached to a message: icon, name (truncated), size, and optionally a remove button. */
export function AttachmentChip({ attachment, onRemove, testId }: AttachmentChipProps) {
  const size = formatBytes(attachment.sizeBytes);
  return (
    <span
      data-testid={testId}
      title={`${attachment.name} (${size})`}
      className={cx(
        'inline-flex h-6 max-w-[200px] items-center gap-1.5 rounded-md border border-line bg-card-2 pl-1.5 text-[11px] text-fg',
        onRemove ? 'pr-0.5' : 'pr-1.5',
      )}
    >
      <AttachmentIcon mediaType={attachment.mediaType} />
      <span className="min-w-0 truncate">{attachment.name}</span>
      <span className="shrink-0 text-fg-faint">{size}</span>
      {onRemove ? (
        <button
          type="button"
          onClick={onRemove}
          aria-label={`Remove ${attachment.name}`}
          title="Remove"
          className="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded text-fg-dim hover:bg-hover hover:text-fg"
        >
          <X size={11} aria-hidden />
        </button>
      ) : null}
    </span>
  );
}

/** A message the user sent: a right-aligned bubble with its attachments above the text. */
export const UserMessage = memo(function UserMessage({ item }: { item: UserMessageItem }) {
  const hasText = item.text.trim().length > 0;
  return (
    <div data-testid="message-user" className="flex flex-col items-end gap-1.5 pl-10">
      {item.attachments.length > 0 ? (
        <div className="flex max-w-full flex-wrap justify-end gap-1">
          {item.attachments.map((attachment, index) => (
            <AttachmentChip key={`${attachment.name}-${index}`} attachment={attachment} testId="message-attachment" />
          ))}
        </div>
      ) : null}
      {hasText ? (
        <div className="selectable max-w-full whitespace-pre-wrap rounded-2xl rounded-br-md border border-line bg-card-2 px-3 py-2 wrap-anywhere">
          {item.text}
        </div>
      ) : null}
    </div>
  );
});

/** Assistant text, rendered as markdown while it streams and afterwards. */
export const AssistantMessage = memo(function AssistantMessage({ item }: { item: AssistantMessageItem }) {
  return (
    <div data-testid="message-assistant" data-streaming={item.streaming ? 'true' : 'false'} className="min-w-0">
      <Markdown text={item.text} />
    </div>
  );
});
