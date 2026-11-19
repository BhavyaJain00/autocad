/** Connects the renderer to `window.copilot`: one snapshot on mount, then live updates. */
import { useEffect, useReducer } from 'react';
import type { Dispatch } from 'react';
import type { CopilotBridge, TimelineEntry } from '../../shared/bridge';
import { INITIAL_APP_STATE, appReducer } from '../state/app-state';
import type { AppAction, AppState } from '../state/app-state';

/** Upper bound on how long queued timeline entries wait when no animation frame arrives (hidden window). */
const FLUSH_FALLBACK_MS = 60;

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message.length > 0 ? error.message : String(error);
}

/**
 * Subscribes to the bridge and returns the application state (plus `dispatch`, for values that
 * bridge calls return directly, such as updated settings). Timeline entries are batched per
 * animation frame so a fast token stream costs one render per frame, not one per delta. Every
 * other event flushes the batch first, so updates are applied in the order the main process sent
 * them.
 */
export function useCopilot(bridge: CopilotBridge): { state: AppState; dispatch: Dispatch<AppAction> } {
  const [state, dispatch] = useReducer(appReducer, INITIAL_APP_STATE);

  useEffect(() => {
    let disposed = false;
    let queue: TimelineEntry[] = [];
    let frame: number | null = null;
    let timer: number | null = null;

    const cancelScheduled = (): void => {
      if (frame !== null) window.cancelAnimationFrame(frame);
      if (timer !== null) window.clearTimeout(timer);
      frame = null;
      timer = null;
    };

    const flush = (): void => {
      cancelScheduled();
      if (disposed || queue.length === 0) return;
      const entries = queue;
      queue = [];
      dispatch({ type: 'timeline', entries });
    };

    const schedule = (): void => {
      if (frame !== null || timer !== null) return;
      frame = window.requestAnimationFrame(flush);
      timer = window.setTimeout(flush, FLUSH_FALLBACK_MS);
    };

    const unsubscribers = [
      bridge.onTimeline((entry) => {
        if (disposed) return;
        queue.push(entry);
        schedule();
      }),
      bridge.onConnection((connection) => {
        if (disposed) return;
        flush();
        dispatch({ type: 'connection', connection });
      }),
      bridge.onConnectors((connectors) => {
        if (disposed) return;
        flush();
        dispatch({ type: 'connectors', connectors });
      }),
      bridge.onSettings((settings) => {
        if (disposed) return;
        flush();
        dispatch({ type: 'settings', settings });
      }),
      bridge.onBusy((busy) => {
        if (disposed) return;
        flush();
        dispatch({ type: 'busy', busy });
      }),
    ];

    bridge.getSnapshot().then(
      (snapshot) => {
        if (disposed) return;
        flush();
        dispatch({ type: 'snapshot', snapshot });
      },
      (error: unknown) => {
        if (!disposed) dispatch({ type: 'snapshot-failed', message: errorMessage(error) });
      },
    );

    return () => {
      disposed = true;
      cancelScheduled();
      for (const unsubscribe of unsubscribers) {
        if (typeof unsubscribe === 'function') unsubscribe();
      }
    };
  }, [bridge]);

  return { state, dispatch };
}
