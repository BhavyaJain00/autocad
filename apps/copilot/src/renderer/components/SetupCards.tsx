/** Empty and blocked states: API key needed, no CAD connected, and the welcome with example prompts. */
import { ExternalLink, KeyRound, Plug, RefreshCw, Sparkles } from 'lucide-react';
import type { ReactNode } from 'react';
import type { ConnectionState, ConnectorSummary } from '../../shared/bridge';
import type { ConnectionActions } from '../hooks/useConnectionActions';
import { examplePromptsFor } from '../lib/composer';
import { API_KEY_URL } from '../lib/links';
import { ApiKeyForm } from './ApiKeyForm';
import { useBridge } from './BridgeContext';
import { ConnectorList } from './ConnectorList';
import { Card, IconButton, Spinner, cx } from './ui';

function CardTitle({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <div className="flex items-center gap-2">
      <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-md bg-card-2 text-accent-soft">{icon}</span>
      <h2 className="m-0 min-w-0 flex-1 text-[13px] font-semibold text-fg">{children}</h2>
    </div>
  );
}

/** Shown when no API key is stored (and the scripted test model is not in use). */
export function ApiKeyCard() {
  const bridge = useBridge();
  return (
    <Card data-testid="api-key-card" className="flex flex-col gap-2.5 p-3.5">
      <CardTitle icon={<KeyRound size={14} aria-hidden />}>Add your Anthropic API key</CardTitle>
      <p className="m-0 text-[12.5px] text-fg-dim">
        CAD Copilot uses Claude to understand your requests, so it needs an Anthropic API key. The key is checked,
        then stored encrypted on this computer.
      </p>
      <ApiKeyForm inputTestId="api-key-input" saveTestId="api-key-save" submitLabel="Save" />
      <button
        type="button"
        data-testid="api-key-link"
        onClick={() => bridge.openExternal(API_KEY_URL)}
        className="inline-flex items-center gap-1 self-start rounded text-[12px] text-accent-soft underline decoration-accent-soft/40 underline-offset-2 hover:decoration-current"
      >
        Get an API key
        <ExternalLink size={11} aria-hidden />
      </button>
    </Card>
  );
}

interface ConnectCardProps {
  connection: ConnectionState;
  connectors: readonly ConnectorSummary[];
  actions: ConnectionActions;
  /** False while the header popover is open (it then owns the connector test ids). */
  withTestIds: boolean;
}

/** Shown when no CAD application is connected: the detected applications, or how to get one listed. */
export function ConnectCard({ connection, connectors, actions, withTestIds }: ConnectCardProps) {
  const connecting = connection.status === 'connecting' || actions.connectingId !== null;
  const failure = actions.error ?? (connection.status === 'error' ? connection.message : null);
  return (
    <Card data-testid="connect-card" className="flex flex-col gap-2.5 p-3.5">
      <div className="flex items-center gap-2">
        <div className="min-w-0 flex-1">
          <CardTitle icon={<Plug size={14} aria-hidden />}>
            {connecting ? 'Connecting...' : 'Connect a CAD application'}
          </CardTitle>
        </div>
        <IconButton label="Look for CAD applications again" data-testid="connect-card-refresh" disabled={actions.refreshing} onClick={actions.refresh}>
          {actions.refreshing ? <Spinner size={13} /> : <RefreshCw size={13} aria-hidden />}
        </IconButton>
      </div>
      {connectors.length > 0 ? (
        <p className="m-0 text-[12.5px] text-fg-dim">
          The copilot works inside a running CAD application. Choose the one to work in:
        </p>
      ) : null}
      <ConnectorList
        connectors={connectors}
        connection={connection}
        connectingId={actions.connectingId}
        onConnect={actions.connect}
        withTestIds={withTestIds}
      />
      {failure !== null ? (
        <p role="alert" className="selectable m-0 text-[12px] text-danger wrap-anywhere">
          {failure}
        </p>
      ) : null}
    </Card>
  );
}

interface WelcomeCardProps {
  cadName: string;
  drawingName: string | null;
  /** The drawing's unit: the example prompts use matching sizes. */
  drawingUnit: string | null;
  onPickPrompt: (prompt: string) => void;
}

/** Shown when connected and nothing has been said yet. */
export function WelcomeCard({ cadName, drawingName, drawingUnit, onPickPrompt }: WelcomeCardProps) {
  return (
    <div data-testid="welcome-card" className="flex flex-col gap-3 px-0.5 pt-2">
      <div className="flex items-center gap-2">
        <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-accent/15 text-accent-soft">
          <Sparkles size={15} aria-hidden />
        </span>
        <h2 className="m-0 text-[15px] font-semibold text-fg">What should we draw?</h2>
      </div>
      <p className="m-0 text-fg-dim wrap-anywhere">
        Describe what you need and the copilot does the work in {cadName}
        {drawingName ? (
          <>
            {' '}
            on <span className="text-fg">{drawingName}</span>
          </>
        ) : null}
        . It asks when something is unclear and shows every change it makes.
      </p>
      <div className="flex flex-col gap-1.5">
        {examplePromptsFor(drawingUnit).map((prompt) => (
          <button
            key={prompt}
            type="button"
            data-testid="example-prompt"
            onClick={() => onPickPrompt(prompt)}
            className={cx(
              'rounded-lg border border-line bg-card px-3 py-2 text-left text-fg transition-colors',
              'hover:border-line-strong hover:bg-hover',
            )}
          >
            {prompt}
          </button>
        ))}
      </div>
    </div>
  );
}
