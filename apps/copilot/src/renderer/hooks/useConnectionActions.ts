/** Connect / disconnect / dock / refresh actions with their pending and error state. */
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ConnectorSummary, CopilotBridge } from '../../shared/bridge';

export interface ConnectionActions {
  connect: (connectorId: string) => void;
  disconnect: () => void;
  setDocked: (docked: boolean) => void;
  refresh: () => void;
  /** Id of the connector a connect request is running for, or null. */
  connectingId: string | null;
  refreshing: boolean;
  docking: boolean;
  /** The last failure, in words, or null. */
  error: string | null;
  clearError: () => void;
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message.length > 0 ? error.message : String(error);
}

/**
 * Wraps the bridge's connection calls so the header popover and the "not connected" card share
 * one pending / error state. The connection itself arrives through `onConnection`.
 */
export function useConnectionActions(
  bridge: CopilotBridge,
  onConnectors: (connectors: ConnectorSummary[]) => void,
): ConnectionActions {
  const [connectingId, setConnectingId] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [docking, setDocking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const connect = useCallback(
    (connectorId: string) => {
      setError(null);
      setConnectingId(connectorId);
      bridge
        .connect(connectorId)
        .then(
          (result) => {
            if (mounted.current && !result.ok) setError(result.message);
          },
          (failure: unknown) => {
            if (mounted.current) setError(errorMessage(failure));
          },
        )
        .finally(() => {
          if (mounted.current) setConnectingId((current) => (current === connectorId ? null : current));
        });
    },
    [bridge],
  );

  const disconnect = useCallback(() => {
    setError(null);
    bridge.disconnect();
  }, [bridge]);

  const setDocked = useCallback(
    (docked: boolean) => {
      setError(null);
      setDocking(true);
      bridge
        .setDocked(docked)
        .then(
          (result) => {
            if (mounted.current && !result.ok) setError(result.message);
          },
          (failure: unknown) => {
            if (mounted.current) setError(errorMessage(failure));
          },
        )
        .finally(() => {
          if (mounted.current) setDocking(false);
        });
    },
    [bridge],
  );

  const refresh = useCallback(() => {
    setError(null);
    setRefreshing(true);
    bridge
      .refreshConnectors()
      .then(
        (connectors) => {
          if (mounted.current && Array.isArray(connectors)) onConnectors(connectors);
        },
        (failure: unknown) => {
          if (mounted.current) setError(errorMessage(failure));
        },
      )
      .finally(() => {
        if (mounted.current) setRefreshing(false);
      });
  }, [bridge, onConnectors]);

  const clearError = useCallback(() => setError(null), []);

  return { connect, disconnect, setDocked, refresh, connectingId, refreshing, docking, error, clearError };
}
