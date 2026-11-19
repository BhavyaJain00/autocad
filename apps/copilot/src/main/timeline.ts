/**
 * The conversation timeline kept by the main process: every session event and user action gets a
 * sequence number and a timestamp, is sent live to the renderer and is recorded so a renderer
 * that starts or reloads later can rebuild the transcript from a snapshot. No Electron imports.
 */
import type { TimelineEntry } from '../shared/bridge';

/** A timeline entry before it is stamped: any member of the TimelineEntry union without seq / at. */
export type TimelineEvent = TimelineEntry extends infer Entry
  ? Entry extends unknown
    ? Omit<Entry, 'seq' | 'at'>
    : never
  : never;

/** Entries kept for snapshots; beyond this the oldest are dropped (the live stream is unaffected). */
export const TIMELINE_MAX_ENTRIES = 5_000;

export interface TimelineRecorderOptions {
  /** Clock for the `at` stamps (tests). */
  readonly now?: () => Date;
  /** Overrides {@link TIMELINE_MAX_ENTRIES}. */
  readonly maxEntries?: number;
}

/**
 * Stamps and records timeline events.
 *
 * Snapshots coalesce streaming text: the 'message.delta' events of one message are kept as a
 * single 'message.delta' entry holding the text so far (at the position of the first delta,
 * carrying the seq / at of the latest one), and that entry is replaced in place by the
 * 'message.done' entry when the message completes. Everything else is recorded as is, in order.
 */
export class TimelineRecorder {
  readonly #now: () => Date;
  readonly #maxEntries: number;
  #seq = 0;
  #entries: TimelineEntry[] = [];
  /** Recorded (coalesced) entry of each message that is still streaming, by messageId. */
  readonly #streaming = new Map<string, TimelineEntry & { type: 'message.delta' }>();

  constructor(options: TimelineRecorderOptions = {}) {
    this.#now = options.now ?? (() => new Date());
    const max = options.maxEntries ?? TIMELINE_MAX_ENTRIES;
    this.#maxEntries = Number.isFinite(max) && max >= 1 ? Math.floor(max) : TIMELINE_MAX_ENTRIES;
  }

  /** Sequence number of the most recent entry (0 before the first one). Never reset. */
  get lastSeq(): number {
    return this.#seq;
  }

  /** Stamps an event, records it and returns the live entry to send to the renderer. */
  record(event: TimelineEvent): TimelineEntry {
    this.#seq += 1;
    const entry = { ...event, seq: this.#seq, at: this.#now().toISOString() } as TimelineEntry;

    if (entry.type === 'message.delta') {
      const streaming = this.#streaming.get(entry.messageId);
      if (streaming !== undefined) {
        streaming.text += entry.text;
        streaming.seq = entry.seq;
        streaming.at = entry.at;
      } else {
        const recorded = { ...entry };
        this.#streaming.set(entry.messageId, recorded);
        this.#append(recorded);
      }
      return entry;
    }

    if (entry.type === 'message.done') {
      const streaming = this.#streaming.get(entry.messageId);
      this.#streaming.delete(entry.messageId);
      const index = streaming === undefined ? -1 : this.#entries.lastIndexOf(streaming);
      if (index >= 0) this.#entries[index] = { ...entry };
      else this.#append({ ...entry });
      return entry;
    }

    this.#append({ ...entry });
    return entry;
  }

  /**
   * Starts a new conversation: forgets everything recorded and records a 'session.reset' entry,
   * which is returned for the live stream (and is the first entry of later snapshots).
   */
  reset(): TimelineEntry {
    this.#entries = [];
    this.#streaming.clear();
    return this.record({ type: 'session.reset' });
  }

  /** The recorded timeline, in transcript order, as independent copies. */
  snapshot(): TimelineEntry[] {
    return this.#entries.map((entry) => ({ ...entry }));
  }

  #append(entry: TimelineEntry): void {
    this.#entries.push(entry);
    const overflow = this.#entries.length - this.#maxEntries;
    if (overflow <= 0) return;
    for (const dropped of this.#entries.splice(0, overflow)) {
      if (dropped.type === 'message.delta' && this.#streaming.get(dropped.messageId) === dropped) {
        this.#streaming.delete(dropped.messageId);
      }
    }
  }
}
