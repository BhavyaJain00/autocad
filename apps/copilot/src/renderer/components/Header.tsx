/** The 40 px title bar: product name, connection chip, New chat, Settings, TEST MODEL badge. */
import { Settings, Sparkles, SquarePen } from 'lucide-react';
import type { RefObject } from 'react';
import type { ConnectionState, ConnectorSummary } from '../../shared/bridge';
import { connectionLabel } from '../lib/format';
import { IconButton, Spinner, cx } from './ui';

interface HeaderProps {
  connection: ConnectionState;
  connectors: readonly ConnectorSummary[];
  testModel: boolean;
  popoverOpen: boolean;
  settingsOpen: boolean;
  /** The chip button, so the popover can ignore clicks on it and return focus to it. */
  chipRef: RefObject<HTMLButtonElement | null>;
  onTogglePopover: () => void;
  onNewChat: () => void;
  onOpenSettings: () => void;
}

const DOT_TONE: Record<'connected' | 'idle' | 'error', string> = {
  connected: 'bg-success',
  idle: 'bg-fg-faint',
  error: 'bg-danger',
};

/**
 * The header is the window's drag region. The native minimise / close buttons are drawn over its
 * right end by the main process, so the right 140 px stay empty; every control opts out of
 * dragging.
 */
export function Header({
  connection,
  connectors,
  testModel,
  popoverOpen,
  settingsOpen,
  chipRef,
  onTogglePopover,
  onNewChat,
  onOpenSettings,
}: HeaderProps) {
  const connectingName =
    connection.status === 'connecting'
      ? connectors.find((connector) => connector.id === connection.connectorId)?.displayName
      : undefined;
  const label = connectionLabel(connection, connectingName);

  return (
    <header className="app-drag @container flex h-10 shrink-0 items-center border-b border-line bg-app pl-3 pr-[140px]">
      <div className="flex min-w-0 flex-1 items-center gap-1.5">
        {/* The name needs room the connection chip uses better in a narrow window: the mark stands in for it. */}
        <div className="flex shrink-0 items-center" title="CAD Copilot">
          <span
            aria-hidden
            className={cx(
              'flex h-5 w-5 items-center justify-center rounded-md bg-accent/20 text-accent-soft',
              testModel ? '@min-[400px]:hidden' : '@min-[300px]:hidden',
            )}
          >
            <Sparkles size={12} />
          </span>
          <h1
            className={cx(
              'm-0 hidden whitespace-nowrap text-[13px] font-semibold text-fg',
              testModel ? '@min-[400px]:block' : '@min-[300px]:block',
            )}
          >
            CAD Copilot
          </h1>
        </div>

        <button
          ref={chipRef}
          type="button"
          data-testid="connection-chip"
          data-status={connection.status}
          aria-haspopup="dialog"
          aria-expanded={popoverOpen}
          aria-label={`${label.description}. Open connection options`}
          title={label.description}
          onClick={onTogglePopover}
          className={cx(
            'app-no-drag ml-1 inline-flex h-6 min-w-0 shrink items-center gap-1.5 rounded-full border pl-2 pr-2.5 text-[12px] transition-colors',
            popoverOpen ? 'border-line-strong bg-hover' : 'border-line bg-card hover:border-line-strong hover:bg-hover',
          )}
        >
          {label.tone === 'busy' ? (
            <Spinner size={11} className="text-fg-dim" />
          ) : (
            <span aria-hidden className={cx('h-2 w-2 shrink-0 rounded-full', DOT_TONE[label.tone])} />
          )}
          {/*
            The drawing name takes only the room the CAD name leaves (zero basis), so it is the one
            that gives way; the CAD name is truncated only when there is no room at all.
          */}
          <span className={cx('min-w-0 shrink truncate', label.tone === 'connected' ? 'text-fg' : 'text-fg-dim')}>
            {label.primary}
          </span>
          {label.secondary !== null ? (
            <span
              className={cx(
                'hidden min-w-0 flex-1 basis-0 truncate text-fg-dim',
                testModel ? '@min-[380px]:block' : '@min-[280px]:block',
              )}
            >
              <span aria-hidden>&middot; </span>
              {label.secondary}
            </span>
          ) : null}
        </button>

        {testModel ? (
          <span
            data-testid="test-model-badge"
            title="A scripted test model is answering instead of Claude"
            className="shrink-0 whitespace-nowrap rounded border border-warning/50 bg-warning/10 px-1 text-[9px] font-bold leading-4 tracking-[0.06em] text-warning"
          >
            TEST<span className="hidden @min-[270px]:inline"> MODEL</span>
          </span>
        ) : null}

        <span className="min-w-0 flex-1" />

        <IconButton
          label="New chat"
          data-testid="new-chat"
          className="app-no-drag"
          onClick={onNewChat}
        >
          <SquarePen size={15} aria-hidden />
        </IconButton>
        <IconButton
          label="Settings"
          data-testid="settings-open"
          className="app-no-drag"
          aria-haspopup="dialog"
          aria-expanded={settingsOpen}
          onClick={onOpenSettings}
        >
          <Settings size={15} aria-hidden />
        </IconButton>
      </div>
    </header>
  );
}
