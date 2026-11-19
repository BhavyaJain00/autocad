/** CAD Copilot's window: header, conversation, pinned task progress and composer. */
import { MonitorX } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type { QuestionAnswer } from '@cadstudio/copilot-agent';
import type { ConnectorSummary, CopilotBridge, SettingsPatch } from '../shared/bridge';
import { BridgeProvider } from './components/BridgeContext';
import { Composer } from './components/Composer';
import { ConnectionPopover } from './components/ConnectionPopover';
import { Conversation } from './components/Conversation';
import { Header } from './components/Header';
import { SettingsDialog } from './components/SettingsDialog';
import { TaskProgress } from './components/TaskProgress';
import { Button, Spinner } from './components/ui';
import { useConnectionActions } from './hooks/useConnectionActions';
import { useCopilot } from './hooks/useCopilot';
import type { ApprovalDecision } from './state/transcript';

/** The bridge the preload script exposes, or null when the page runs outside CAD Copilot. */
function findBridge(): CopilotBridge | null {
  if (typeof window === 'undefined') return null;
  const candidate = (window as { copilot?: CopilotBridge }).copilot;
  return candidate && typeof candidate.getSnapshot === 'function' ? candidate : null;
}

function Shell({ children }: { children: ReactNode }) {
  return (
    <div data-testid="copilot-root" className="flex h-full flex-col bg-app text-fg">
      {children}
    </div>
  );
}

/** A bare header strip so the window can still be dragged while nothing else is available. */
function BareHeader() {
  return (
    <header className="app-drag flex h-10 shrink-0 items-center border-b border-line pl-3 pr-[140px]">
      <h1 className="m-0 text-[13px] font-semibold text-fg">CAD Copilot</h1>
    </header>
  );
}

function CenteredMessage({ children }: { children: ReactNode }) {
  return <div className="flex min-h-0 flex-1 flex-col items-center justify-center gap-3 px-8 text-center">{children}</div>;
}

function NoBridge() {
  return (
    <Shell>
      <BareHeader />
      <CenteredMessage>
        <MonitorX size={28} aria-hidden className="text-fg-faint" />
        <h2 data-testid="no-bridge" className="m-0 text-[15px] font-semibold text-fg">
          This interface runs inside CAD Copilot
        </h2>
        <p className="m-0 max-w-[320px] text-fg-dim">
          It was opened in a plain browser, where it cannot reach the copilot or a CAD application. Start the CAD
          Copilot desktop app instead.
        </p>
      </CenteredMessage>
    </Shell>
  );
}

function CopilotWindow({ bridge }: { bridge: CopilotBridge }) {
  const { state, dispatch } = useCopilot(bridge);
  const { settings, connection, connectors, busy, transcript } = state;

  const [draft, setDraft] = useState('');
  const [popoverOpen, setPopoverOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const chipRef = useRef<HTMLButtonElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);

  const onConnectors = useCallback(
    (list: ConnectorSummary[]) => dispatch({ type: 'connectors', connectors: list }),
    [dispatch],
  );
  const connectionActions = useConnectionActions(bridge, onConnectors);
  const clearConnectionError = connectionActions.clearError;

  // A connection that succeeded makes an earlier failure message stale.
  const connectionStatus = connection.status;
  useEffect(() => {
    if (connectionStatus === 'connected') clearConnectionError();
  }, [connectionStatus, clearConnectionError]);

  // The message field is where typing should land as soon as the window is usable.
  const ready = state.phase === 'ready' && settings !== null;
  const canChat = ready && (settings.hasApiKey || settings.testModel);
  useEffect(() => {
    if (canChat) textareaRef.current?.focus();
  }, [canChat]);

  // A file dropped on the window must never navigate away from the app (attachments use the picker).
  useEffect(() => {
    const block = (event: DragEvent): void => event.preventDefault();
    window.addEventListener('dragover', block);
    window.addEventListener('drop', block);
    return () => {
      window.removeEventListener('dragover', block);
      window.removeEventListener('drop', block);
    };
  }, []);

  const closePopover = useCallback(() => setPopoverOpen(false), []);
  const closeSettings = useCallback(() => setSettingsOpen(false), []);

  const updateSettings = useCallback(
    (patch: SettingsPatch) => {
      setSettingsError(null);
      bridge.updateSettings(patch).then(
        (next) => {
          if (next && typeof next === 'object') dispatch({ type: 'settings', settings: next });
        },
        (failure: unknown) => {
          setSettingsError(failure instanceof Error ? failure.message : String(failure));
        },
      );
    },
    [bridge, dispatch],
  );

  const answerQuestion = useCallback(
    (requestId: string, answers: QuestionAnswer[]) => bridge.answerQuestion(requestId, answers),
    [bridge],
  );
  const resolveApproval = useCallback(
    (requestId: string, decision: ApprovalDecision) => bridge.resolveApproval(requestId, decision),
    [bridge],
  );

  const pickPrompt = useCallback((prompt: string) => {
    setDraft(prompt);
    const field = textareaRef.current;
    if (field) {
      field.focus();
      // Put the caret at the end once React has written the new value.
      window.requestAnimationFrame(() => field.setSelectionRange(prompt.length, prompt.length));
    }
  }, []);

  const newChat = useCallback(() => {
    bridge.resetSession();
    textareaRef.current?.focus();
  }, [bridge]);

  if (state.phase === 'failed') {
    return (
      <Shell>
        <BareHeader />
        <CenteredMessage>
          <h2 className="m-0 text-[15px] font-semibold text-fg">CAD Copilot could not start</h2>
          <p role="alert" className="selectable m-0 max-w-[320px] text-fg-dim wrap-anywhere">
            {state.loadError ?? 'The application state could not be read.'}
          </p>
          <Button variant="secondary" onClick={() => window.location.reload()}>
            Try again
          </Button>
        </CenteredMessage>
      </Shell>
    );
  }

  if (state.phase === 'loading' || settings === null) {
    return (
      <Shell>
        <BareHeader />
        <CenteredMessage>
          <Spinner size={18} className="text-fg-dim" label="Starting CAD Copilot" />
        </CenteredMessage>
      </Shell>
    );
  }

  const contextHint =
    settingsError ??
    (connection.status === 'connected'
      ? null
      : 'No CAD application is connected: the copilot can answer, but it cannot draw.');

  return (
    <BridgeProvider bridge={bridge}>
      <Shell>
        <Header
          connection={connection}
          connectors={connectors}
          testModel={settings.testModel}
          popoverOpen={popoverOpen}
          settingsOpen={settingsOpen}
          chipRef={chipRef}
          onTogglePopover={() => setPopoverOpen((open) => !open)}
          onNewChat={newChat}
          onOpenSettings={() => setSettingsOpen((open) => !open)}
        />
        {popoverOpen ? (
          <ConnectionPopover
            connection={connection}
            connectors={connectors}
            actions={connectionActions}
            anchorRef={chipRef}
            onClose={closePopover}
          />
        ) : null}

        <Conversation
          transcript={transcript}
          busy={busy}
          settings={settings}
          connection={connection}
          connectors={connectors}
          connectionActions={connectionActions}
          popoverOpen={popoverOpen}
          onPickPrompt={pickPrompt}
          onAnswer={answerQuestion}
          onResolveApproval={resolveApproval}
        />

        {transcript.livePlan !== null ? (
          <div className="shrink-0 px-3 pt-1">
            <TaskProgress key={transcript.livePlan.key} tasks={transcript.livePlan.tasks} live />
          </div>
        ) : null}

        <Composer
          text={draft}
          onTextChange={setDraft}
          textareaRef={textareaRef}
          settings={settings}
          busy={busy}
          contextHint={contextHint}
          onUpdateSettings={updateSettings}
        />

        {settingsOpen ? (
          <SettingsDialog
            settings={settings}
            appVersion={state.appVersion}
            onUpdateSettings={updateSettings}
            onClose={closeSettings}
          />
        ) : null}
      </Shell>
    </BridgeProvider>
  );
}

/** Root component: the copilot window, or an explanation when the page runs outside the app. */
export function App() {
  const [bridge] = useState(findBridge);
  return bridge === null ? <NoBridge /> : <CopilotWindow bridge={bridge} />;
}
