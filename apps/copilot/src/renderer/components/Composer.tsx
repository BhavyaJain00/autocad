/** The composer: attachments, the message field, model and effort pickers, Send / Stop. */
import { ArrowUp, Plus, Square } from 'lucide-react';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { KeyboardEvent, RefObject } from 'react';
import type { Effort, ModelId } from '@cadstudio/copilot-agent';
import { ATTACHMENT_MAX_COUNT, EFFORT_CHOICES, MODEL_CHOICES } from '../../shared/bridge';
import type { ActionResult, CopilotSettings, PickedAttachment, SettingsPatch } from '../../shared/bridge';
import { isSendKey, mergeAttachments, removeAttachment, sendBlock } from '../lib/composer';
import { inputsLabel } from '../lib/format';
import { useBridge } from './BridgeContext';
import { AttachmentChip } from './Messages';
import { Spinner, cx } from './ui';

/** Tallest the message field grows before it scrolls (about eight lines). */
const TEXTAREA_MAX_HEIGHT = 168;

interface ComposerProps {
  text: string;
  onTextChange: (text: string) => void;
  textareaRef: RefObject<HTMLTextAreaElement | null>;
  settings: CopilotSettings;
  busy: boolean;
  /** A short remark shown under the field when nothing more important is (for example "no CAD connected"). */
  contextHint: string | null;
  onUpdateSettings: (patch: SettingsPatch) => void;
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message.length > 0 ? error.message : String(error);
}

export function Composer({
  text,
  onTextChange,
  textareaRef,
  settings,
  busy,
  contextHint,
  onUpdateSettings,
}: ComposerProps) {
  const bridge = useBridge();
  const [attachments, setAttachments] = useState<PickedAttachment[]>([]);
  const [sending, setSending] = useState(false);
  const [picking, setPicking] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const mounted = useRef(true);
  // The file picker resolves later; it must merge into the attachments of that moment.
  const attachmentsRef = useRef(attachments);
  useLayoutEffect(() => {
    attachmentsRef.current = attachments;
  }, [attachments]);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  // Grow with the text up to a limit, then scroll inside the field.
  useLayoutEffect(() => {
    const field = textareaRef.current;
    if (!field) return;
    field.style.height = 'auto';
    const height = Math.min(field.scrollHeight, TEXTAREA_MAX_HEIGHT);
    field.style.height = `${height}px`;
    field.style.overflowY = field.scrollHeight > TEXTAREA_MAX_HEIGHT ? 'auto' : 'hidden';
  }, [text, textareaRef]);

  const block = sendBlock({
    text,
    attachmentCount: attachments.length,
    busy,
    sending,
    hasApiKey: settings.hasApiKey,
    testModel: settings.testModel,
  });

  const send = (): void => {
    if (block !== null) return;
    const message = text.trim();
    const files = attachments;
    setSending(true);
    setProblem(null);
    setNotice(null);
    bridge
      .sendMessage(message, files)
      .then(
        (result: ActionResult) => {
          if (!mounted.current) return;
          if (result.ok) {
            onTextChange('');
            setAttachments([]);
          } else {
            setProblem(result.message);
          }
        },
        (failure: unknown) => {
          if (mounted.current) setProblem(errorMessage(failure));
        },
      )
      .finally(() => {
        if (!mounted.current) return;
        setSending(false);
        textareaRef.current?.focus();
      });
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>): void => {
    if (!isSendKey({ ...pickKeys(event), isComposing: event.nativeEvent.isComposing })) return;
    // Enter never inserts a newline; it sends when sending is possible.
    event.preventDefault();
    send();
  };

  const pick = (): void => {
    if (picking) return;
    setPicking(true);
    setNotice(null);
    bridge
      .pickAttachments()
      .then(
        (picked) => {
          if (!mounted.current || !Array.isArray(picked) || picked.length === 0) return;
          const merged = mergeAttachments(attachmentsRef.current, picked);
          setAttachments(merged.attachments);
          setNotice(merged.notice);
        },
        (failure: unknown) => {
          if (mounted.current) setNotice(errorMessage(failure));
        },
      )
      .finally(() => {
        if (mounted.current) setPicking(false);
      });
  };

  const attachmentsFull = attachments.length >= ATTACHMENT_MAX_COUNT;
  const hint = problem ?? notice ?? (block?.prominent ? block.reason : null) ?? contextHint;
  const hintTone = problem !== null ? 'text-danger' : notice !== null ? 'text-warning' : 'text-fg-faint';

  return (
    <div className="shrink-0 px-3 pb-3 pt-1">
      <div className="rounded-xl border border-line bg-card transition-colors focus-within:border-line-strong">
        {attachments.length > 0 ? (
          <div className="flex flex-wrap gap-1 px-2.5 pt-2.5" aria-label="Attachments">
            {attachments.map((attachment, index) => (
              <AttachmentChip
                key={`${attachment.name}-${attachment.sizeBytes}-${index}`}
                attachment={attachment}
                testId="attachment-chip"
                onRemove={() => {
                  setAttachments((current) => removeAttachment(current, index));
                  setNotice(null);
                }}
              />
            ))}
          </div>
        ) : null}

        <textarea
          ref={textareaRef}
          value={text}
          rows={1}
          placeholder="Ask for anything..."
          aria-label="Message"
          data-testid="composer-input"
          spellCheck
          onChange={(event) => {
            onTextChange(event.target.value);
            if (problem !== null) setProblem(null);
          }}
          onKeyDown={onKeyDown}
          className="selectable block w-full resize-none bg-transparent px-3 pb-1 pt-2.5 leading-[1.45] text-fg outline-none focus-visible:outline-none"
        />

        <div className="flex items-center gap-1 px-1.5 pb-1.5">
          <button
            type="button"
            data-testid="attach-btn"
            aria-label="Attach images, PDFs or text files"
            title={
              attachmentsFull
                ? `A message can carry up to ${ATTACHMENT_MAX_COUNT} attachments`
                : 'Attach images, PDFs or text files'
            }
            disabled={picking || attachmentsFull}
            onClick={pick}
            className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-fg-dim transition-colors hover:bg-hover hover:text-fg disabled:opacity-40 disabled:hover:bg-transparent"
          >
            {picking ? <Spinner size={13} /> : <Plus size={15} aria-hidden />}
          </button>
          {attachments.length > 0 ? (
            <span data-testid="attachment-count" className="shrink-0 whitespace-nowrap pr-1 text-[11px] text-fg-dim">
              {inputsLabel(attachments.length)}
            </span>
          ) : null}

          <select
            data-testid="model-select"
            aria-label="Model"
            title={MODEL_CHOICES.find((choice) => choice.id === settings.model)?.hint ?? 'Model'}
            value={settings.model}
            onChange={(event) => onUpdateSettings({ model: event.target.value as ModelId })}
            className="select-pill h-7 min-w-0 max-w-[150px] shrink rounded-md text-[12px] text-fg-dim transition-colors hover:bg-hover hover:text-fg"
          >
            {MODEL_CHOICES.map((choice) => (
              <option key={choice.id} value={choice.id} title={choice.hint}>
                {choice.label}
              </option>
            ))}
          </select>
          <select
            data-testid="effort-select"
            aria-label="Reasoning effort"
            title="Reasoning effort: higher thinks longer before acting"
            value={settings.effort}
            onChange={(event) => onUpdateSettings({ effort: event.target.value as Effort })}
            className="select-pill h-7 min-w-0 max-w-[104px] shrink rounded-md text-[12px] text-fg-dim transition-colors hover:bg-hover hover:text-fg"
          >
            {EFFORT_CHOICES.map((choice) => (
              <option key={choice.id} value={choice.id}>
                {choice.label}
              </option>
            ))}
          </select>

          <span className="min-w-0 flex-1" />

          {busy ? (
            <button
              type="button"
              data-testid="composer-stop"
              aria-label="Stop"
              title="Stop"
              onClick={() => bridge.stop()}
              className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-fg text-app transition-colors hover:bg-white"
            >
              <Square size={11} fill="currentColor" aria-hidden />
            </button>
          ) : (
            <button
              type="button"
              data-testid="composer-send"
              aria-label="Send"
              title={block?.reason ?? 'Send (Enter)'}
              disabled={block !== null}
              onClick={send}
              className={cx(
                'inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-lg transition-colors',
                block === null ? 'bg-accent text-on-accent hover:bg-accent-hover' : 'bg-card-2 text-fg-faint',
              )}
            >
              {sending ? <Spinner size={13} /> : <ArrowUp size={15} strokeWidth={2.5} aria-hidden />}
            </button>
          )}
        </div>
      </div>
      <div
        data-testid="composer-hint"
        role={problem !== null ? 'alert' : 'status'}
        className={cx('min-h-[18px] px-1 pt-1 text-[11px] leading-[1.35] wrap-anywhere', hintTone)}
      >
        {hint}
      </div>
    </div>
  );
}

function pickKeys(event: KeyboardEvent): { key: string; shiftKey: boolean; ctrlKey: boolean; altKey: boolean; metaKey: boolean } {
  return {
    key: event.key,
    shiftKey: event.shiftKey,
    ctrlKey: event.ctrlKey,
    altKey: event.altKey,
    metaKey: event.metaKey,
  };
}
