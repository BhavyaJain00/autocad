/** The one scroll area: the transcript, or the setup / welcome cards when there is nothing to show yet. */
import { ArrowDown } from 'lucide-react';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { QuestionAnswer } from '@cadstudio/copilot-agent';
import type { ConnectionState, ConnectorSummary, CopilotSettings } from '../../shared/bridge';
import type { ConnectionActions } from '../hooks/useConnectionActions';
import { cadDisplayName } from '../lib/format';
import { hasVisibleProgress, isTranscriptEmpty, newestRunningCallId } from '../state/transcript';
import type { ApprovalDecision, TranscriptItem, TranscriptState } from '../state/transcript';
import { ActivityGroup } from './ActivityGroup';
import { ApprovalCard } from './ApprovalCard';
import { AssistantMessage, UserMessage } from './Messages';
import { ErrorNotice, ThinkingIndicator, TurnFooter } from './Notices';
import { QuestionCard } from './QuestionCard';
import { ApiKeyCard, ConnectCard, WelcomeCard } from './SetupCards';
import { TaskProgress } from './TaskProgress';

/** Within this many pixels of the end, the view counts as "at the bottom" and follows new content. */
const STICK_THRESHOLD_PX = 28;

interface ConversationProps {
  transcript: TranscriptState;
  busy: boolean;
  settings: CopilotSettings;
  connection: ConnectionState;
  connectors: readonly ConnectorSummary[];
  connectionActions: ConnectionActions;
  /** True while the header's connection popover is open (it then owns the connector test ids). */
  popoverOpen: boolean;
  onPickPrompt: (prompt: string) => void;
  onAnswer: (requestId: string, answers: QuestionAnswer[]) => void;
  onResolveApproval: (requestId: string, decision: ApprovalDecision) => void;
}

interface ItemViewProps {
  item: TranscriptItem;
  cadName: string;
  runningCallId: string | null;
  onAnswer: (requestId: string, answers: QuestionAnswer[]) => void;
  onResolveApproval: (requestId: string, decision: ApprovalDecision) => void;
}

function ItemView({ item, cadName, runningCallId, onAnswer, onResolveApproval }: ItemViewProps) {
  switch (item.kind) {
    case 'user':
      return <UserMessage item={item} />;
    case 'assistant':
      return <AssistantMessage item={item} />;
    case 'question':
      return <QuestionCard item={item} onSubmit={onAnswer} />;
    case 'activity':
      return <ActivityGroup item={item} cadName={cadName} newestRunningCallId={runningCallId} />;
    case 'approval':
      return <ApprovalCard item={item} onResolve={onResolveApproval} />;
    case 'plan':
      return <TaskProgress tasks={item.tasks} live={false} />;
    case 'error':
      return <ErrorNotice item={item} />;
    case 'turn':
      return <TurnFooter item={item} />;
    default:
      return null;
  }
}

/**
 * Renders the transcript and keeps the view pinned to the newest content while it streams, unless
 * the user scrolled up to read; a "jump to latest" button then brings them back.
 */
export function Conversation({
  transcript,
  busy,
  settings,
  connection,
  connectors,
  connectionActions,
  popoverOpen,
  onPickPrompt,
  onAnswer,
  onResolveApproval,
}: ConversationProps) {
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const contentRef = useRef<HTMLDivElement | null>(null);
  const stick = useRef(true);
  /** The scroll position this component last set (or saw at the end); lower means the user scrolled up. */
  const lastEndTop = useRef(0);
  const [detached, setDetached] = useState(false);

  const scrollToBottom = useCallback(() => {
    const area = scrollRef.current;
    if (!area) return;
    area.scrollTop = area.scrollHeight;
    lastEndTop.current = area.scrollTop;
  }, []);

  /**
   * Keeps the view at the end while attached. Scroll events arrive a frame late, so a scroll the
   * user just made is also recognised here (the position moved up although nothing here moved it):
   * new content never pulls the view back from someone who started reading.
   */
  const follow = useCallback(() => {
    const area = scrollRef.current;
    if (!area) return;
    const distance = area.scrollHeight - area.scrollTop - area.clientHeight;
    if (stick.current) {
      const movedUp = area.scrollTop < lastEndTop.current - 1;
      if (movedUp && distance > STICK_THRESHOLD_PX) {
        stick.current = false;
        setDetached(true);
        return;
      }
      scrollToBottom();
      return;
    }
    // Content can shrink (a new chat) without a scroll event: re-attach when the end is in view again.
    if (distance <= STICK_THRESHOLD_PX) {
      stick.current = true;
      lastEndTop.current = area.scrollTop;
      setDetached(false);
    }
  }, [scrollToBottom]);

  const onScroll = (): void => {
    const area = scrollRef.current;
    if (!area) return;
    const distance = area.scrollHeight - area.scrollTop - area.clientHeight;
    const atBottom = distance <= STICK_THRESHOLD_PX;
    stick.current = atBottom;
    if (atBottom) lastEndTop.current = area.scrollTop;
    setDetached(!atBottom);
  };

  // A message the user just sent always brings the view back to the end.
  const lastUserKey = findLastUserKey(transcript.items);
  useLayoutEffect(() => {
    stick.current = true;
    lastEndTop.current = 0;
    setDetached(false);
  }, [lastUserKey]);

  // Follow new content before the browser paints, so streaming never shows a jump.
  useLayoutEffect(() => {
    follow();
  });

  // Content can also grow without a render of this component (fonts, the pinned plan resizing the area).
  useEffect(() => {
    const area = scrollRef.current;
    const content = contentRef.current;
    if (!area || !content || typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(follow);
    observer.observe(area);
    observer.observe(content);
    return () => observer.disconnect();
  }, [follow]);

  const empty = isTranscriptEmpty(transcript);
  const needsApiKey = !settings.hasApiKey && !settings.testModel;
  const connected = connection.status === 'connected';
  const cadName = cadDisplayName(connection);
  const runningCallId = newestRunningCallId(transcript);
  // After Stop the app stays busy until the turn has wound down; that is not "thinking".
  const thinking = busy && transcript.turnOpen && !hasVisibleProgress(transcript);

  return (
    <div className="relative flex min-h-0 flex-1 flex-col">
      <div
        ref={scrollRef}
        data-testid="conversation"
        role="log"
        aria-label="Conversation"
        aria-live="off"
        tabIndex={0}
        onScroll={onScroll}
        className="min-h-0 flex-1 overflow-y-auto overflow-x-hidden focus-visible:-outline-offset-2"
      >
        <div ref={contentRef} className="flex flex-col gap-3 px-3 pb-3 pt-3">
          {transcript.items.map((item) => (
            <ItemView
              key={item.key}
              item={item}
              cadName={cadName}
              runningCallId={runningCallId}
              onAnswer={onAnswer}
              onResolveApproval={onResolveApproval}
            />
          ))}
          {thinking ? <ThinkingIndicator /> : null}
          {needsApiKey ? <ApiKeyCard /> : null}
          {empty && !connected ? (
            <ConnectCard
              connection={connection}
              connectors={connectors}
              actions={connectionActions}
              withTestIds={!popoverOpen}
            />
          ) : null}
          {empty && connected && !needsApiKey ? (
            <WelcomeCard
              cadName={cadName}
              drawingName={connection.document?.name ?? null}
              drawingUnit={connection.document?.unit ?? null}
              onPickPrompt={onPickPrompt}
            />
          ) : null}
        </div>
      </div>
      {detached ? (
        <button
          type="button"
          data-testid="scroll-to-latest"
          aria-label="Scroll to the latest message"
          title="Scroll to the latest message"
          onClick={() => {
            stick.current = true;
            setDetached(false);
            scrollToBottom();
          }}
          className="absolute bottom-2 left-1/2 inline-flex h-7 w-7 -translate-x-1/2 items-center justify-center rounded-full border border-line-strong bg-card-2 text-fg shadow-[0_4px_14px_rgba(0,0,0,0.5)] transition-colors hover:bg-hover"
        >
          <ArrowDown size={14} aria-hidden />
        </button>
      ) : null}
    </div>
  );
}

function findLastUserKey(items: readonly TranscriptItem[]): string | null {
  for (let i = items.length - 1; i >= 0; i -= 1) {
    const item = items[i];
    if (item?.kind === 'user') return item.key;
  }
  return null;
}
