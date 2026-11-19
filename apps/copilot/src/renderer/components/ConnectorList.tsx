/** The detected CAD applications, each with a Connect button (shared by the popover and the setup card). */
import { Box, Check } from 'lucide-react';
import type { ConnectionState, ConnectorSummary } from '../../shared/bridge';
import { Button, Spinner } from './ui';

interface ConnectorListProps {
  connectors: readonly ConnectorSummary[];
  connection: ConnectionState;
  /** Id of the connector a connect request is running for, or null. */
  connectingId: string | null;
  onConnect: (connectorId: string) => void;
  /**
   * Whether the rows carry the end-to-end test ids. Only one list on screen may carry them, so the
   * setup card gives them up while the header popover is open.
   */
  withTestIds: boolean;
}

/** Message shown when no CAD application is running. */
export const NO_CONNECTORS_TEXT = 'Open CAD Studio (or AutoCAD with the Copilot plug-in) and it will appear here';

export function ConnectorList({ connectors, connection, connectingId, onConnect, withTestIds }: ConnectorListProps) {
  if (connectors.length === 0) {
    return <p className="m-0 text-[12.5px] text-fg-dim">{NO_CONNECTORS_TEXT}</p>;
  }
  const connectedId = connection.status === 'connected' ? connection.connector.id : null;
  const busyId = connectingId ?? (connection.status === 'connecting' ? connection.connectorId : null);

  return (
    <ul className="m-0 flex list-none flex-col gap-1 p-0">
      {connectors.map((connector) => {
        const connected = connector.id === connectedId;
        const connecting = connector.id === busyId;
        return (
          <li
            key={connector.id}
            data-testid={withTestIds ? `connector-item-${connector.id}` : undefined}
            data-connected={connected ? 'true' : 'false'}
            className="flex items-center gap-2 rounded-lg border border-line bg-card-2 px-2.5 py-1.5"
          >
            <Box size={15} aria-hidden className="shrink-0 text-fg-dim" />
            <div className="min-w-0 flex-1">
              <div className="truncate text-fg" title={connector.displayName}>
                {connector.displayName}
              </div>
              <div className="truncate text-[11px] text-fg-faint">
                {connector.cadVersion ? `Version ${connector.cadVersion}, ` : ''}process {connector.pid}
              </div>
            </div>
            {connected ? (
              <span className="inline-flex shrink-0 items-center gap-1 text-[12px] text-success">
                <Check size={13} aria-hidden />
                Connected
              </span>
            ) : (
              <Button
                variant="primary"
                data-testid={withTestIds ? `connector-connect-${connector.id}` : undefined}
                disabled={busyId !== null}
                aria-label={`Connect to ${connector.displayName}`}
                onClick={() => onConnect(connector.id)}
              >
                {connecting ? <Spinner size={12} /> : null}
                {connecting ? 'Connecting...' : 'Connect'}
              </Button>
            )}
          </li>
        );
      })}
    </ul>
  );
}
