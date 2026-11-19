/**
 * Application state of the copilot renderer: the startup snapshot plus the live updates the main
 * process pushes afterwards. Pure reducer (unit-tested in Node); `hooks/useCopilot.ts` wires it to
 * `window.copilot`.
 */
import type {
  ConnectionState,
  ConnectorSummary,
  CopilotSettings,
  CopilotSnapshot,
  TimelineEntry,
} from '../../shared/bridge';
import { INITIAL_TRANSCRIPT, applyEntries, transcriptFromTimeline } from './transcript';
import type { TranscriptState } from './transcript';

export interface AppState {
  /** loading: waiting for the snapshot; ready: live; failed: the snapshot could not be read. */
  phase: 'loading' | 'ready' | 'failed';
  loadError: string | null;
  appVersion: string;
  /** null until the snapshot (or a settings event) arrives. */
  settings: CopilotSettings | null;
  connection: ConnectionState;
  connectors: readonly ConnectorSummary[];
  busy: boolean;
  transcript: TranscriptState;
  /** Live entries received before the snapshot; replayed (minus duplicates) once it arrives. */
  earlyEntries: readonly TimelineEntry[];
}

export type AppAction =
  | { type: 'snapshot'; snapshot: CopilotSnapshot }
  | { type: 'snapshot-failed'; message: string }
  | { type: 'timeline'; entries: readonly TimelineEntry[] }
  | { type: 'connection'; connection: ConnectionState }
  | { type: 'connectors'; connectors: readonly ConnectorSummary[] }
  | { type: 'settings'; settings: CopilotSettings }
  | { type: 'busy'; busy: boolean };

export const INITIAL_APP_STATE: AppState = {
  phase: 'loading',
  loadError: null,
  appVersion: '',
  settings: null,
  connection: { status: 'disconnected' },
  connectors: [],
  busy: false,
  transcript: INITIAL_TRANSCRIPT,
  earlyEntries: [],
};

export function appReducer(state: AppState, action: AppAction): AppState {
  switch (action.type) {
    case 'snapshot': {
      const { snapshot } = action;
      // The snapshot is at least as recent as anything pushed before it was answered, so it
      // replaces the pushed values; early timeline entries it already holds are dropped by `seq`.
      const transcript = applyEntries(transcriptFromTimeline(snapshot.timeline), state.earlyEntries);
      return {
        phase: 'ready',
        loadError: null,
        appVersion: snapshot.appVersion,
        settings: snapshot.settings,
        connection: snapshot.connection,
        connectors: snapshot.connectors,
        busy: snapshot.busy,
        transcript,
        earlyEntries: [],
      };
    }
    case 'snapshot-failed':
      return state.phase === 'ready' ? state : { ...state, phase: 'failed', loadError: action.message };
    case 'timeline': {
      if (action.entries.length === 0) return state;
      if (state.phase !== 'ready') {
        return { ...state, earlyEntries: [...state.earlyEntries, ...action.entries] };
      }
      const transcript = applyEntries(state.transcript, action.entries);
      return transcript === state.transcript ? state : { ...state, transcript };
    }
    case 'connection':
      return { ...state, connection: action.connection };
    case 'connectors':
      return { ...state, connectors: action.connectors };
    case 'settings':
      return { ...state, settings: action.settings };
    case 'busy':
      return state.busy === action.busy ? state : { ...state, busy: action.busy };
    default:
      return state;
  }
}
