/** The popover behind the connection chip: detected CADs, Disconnect, Dock/Undock, Refresh. */
import { PanelRightClose, PanelRightOpen, RefreshCw, Unplug } from 'lucide-react';
import { useEffect, useRef } from 'react';
import type { RefObject } from 'react';
import type { ConnectionState, ConnectorSummary } from '../../shared/bridge';
import type { ConnectionActions } from '../hooks/useConnectionActions';
import { pluralize } from '../lib/format';
import { ConnectorList } from './ConnectorList';
import { Button, IconButton, Spinner } from './ui';

interface ConnectionPopoverProps {
  connection: ConnectionState;
  connectors: readonly ConnectorSummary[];
  actions: ConnectionActions;
  /** The chip that opened the popover; clicks on it are left to its own toggle handler. */
  anchorRef: RefObject<HTMLButtonElement | null>;
  onClose: () => void;
}

function ConnectionSummary({ connection }: { connection: ConnectionState }) {
  if (connection.status === 'connected') {
    const { connector, document: drawing } = connection;
    return (
      <div className="min-w-0">
        <div className="truncate font-medium text-fg" title={connector.displayName}>
          Connected to {connector.displayName}
        </div>
        <div className="truncate text-[12px] text-fg-dim" title={drawing?.path ?? drawing?.name ?? undefined}>
          {drawing
            ? `${drawing.name} (${pluralize(drawing.entityCount, 'object')}, ${drawing.unit})`
            : 'No drawing is open'}
        </div>
      </div>
    );
  }
  if (connection.status === 'connecting') {
    return <div className="font-medium text-fg">Connecting...</div>;
  }
  return (
    <div className="min-w-0">
      <div className="font-medium text-fg">Not connected</div>
      {connection.status === 'error' ? (
        <div role="alert" className="selectable text-[12px] text-danger wrap-anywhere">
          {connection.message}
        </div>
      ) : null}
    </div>
  );
}

/**
 * Lists the running CAD applications and the connection actions. Closes on Escape or a click
 * anywhere outside it.
 */
export function ConnectionPopover({ connection, connectors, actions, anchorRef, onClose }: ConnectionPopoverProps) {
  const panelRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const onPointerDown = (event: MouseEvent): void => {
      const target = event.target;
      if (!(target instanceof Node)) return;
      if (panelRef.current?.contains(target) || anchorRef.current?.contains(target)) return;
      onClose();
    };
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
        anchorRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [anchorRef, onClose]);

  useEffect(() => {
    panelRef.current?.focus();
  }, []);

  const connected = connection.status === 'connected';
  const docked = connected && connection.docked;

  return (
    <div
      ref={panelRef}
      role="dialog"
      aria-label="CAD connection"
      tabIndex={-1}
      data-testid="connection-popover"
      className="animate-pop fixed left-2 right-2 top-11 z-40 mx-auto flex max-h-[calc(100vh-56px)] max-w-[400px] flex-col gap-2.5 overflow-y-auto rounded-xl border border-line-strong bg-card p-3 shadow-[0_12px_32px_rgba(0,0,0,0.55)]"
    >
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1">
          <ConnectionSummary connection={connection} />
        </div>
        <IconButton
          label="Look for CAD applications again"
          data-testid="refresh-connectors"
          disabled={actions.refreshing}
          onClick={actions.refresh}
        >
          {actions.refreshing ? <Spinner size={13} /> : <RefreshCw size={13} aria-hidden />}
        </IconButton>
      </div>

      <div>
        <div className="mb-1 text-[10px] font-semibold uppercase tracking-[0.08em] text-fg-faint">
          CAD applications on this computer
        </div>
        <ConnectorList
          connectors={connectors}
          connection={connection}
          connectingId={actions.connectingId}
          onConnect={actions.connect}
          withTestIds
        />
      </div>

      {actions.error !== null ? (
        <p role="alert" data-testid="connection-error" className="selectable m-0 text-[12px] text-danger wrap-anywhere">
          {actions.error}
        </p>
      ) : null}

      <div className="flex flex-wrap items-center gap-1.5 border-t border-line pt-2.5">
        <Button
          variant="secondary"
          data-testid="dock-toggle"
          data-docked={docked ? 'true' : 'false'}
          aria-pressed={docked}
          disabled={!connected || actions.docking}
          title={
            connected
              ? docked
                ? 'Release the CAD window and restore its previous size'
                : 'Place this panel beside the CAD window'
              : 'Connect to a CAD application to dock beside it'
          }
          onClick={() => actions.setDocked(!docked)}
        >
          {actions.docking ? (
            <Spinner size={12} />
          ) : docked ? (
            <PanelRightOpen size={13} aria-hidden />
          ) : (
            <PanelRightClose size={13} aria-hidden />
          )}
          {docked ? 'Undock' : 'Dock beside CAD'}
        </Button>
        <Button
          variant="secondary"
          data-testid="disconnect-btn"
          disabled={connection.status !== 'connected' && connection.status !== 'connecting'}
          onClick={actions.disconnect}
        >
          <Unplug size={13} aria-hidden />
          Disconnect
        </Button>
      </div>
    </div>
  );
}
