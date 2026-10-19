import { nextEventSeq, uuid } from './device';
import { sendEvents, sendEventsBeforeLeaving } from './protocol';
import type { PlaybackEvent } from './protocol';

const FLUSH_MS = 10000;
const BATCH = 20;
const MAX_BATCH = 100;
const MAX_QUEUE = 500;

export class Telemetry {
  private sessionId: string | null = null;
  private queue: PlaybackEvent[] = [];
  private timer: number | null = null;
  private sending = false;

  bind(sessionId: string | null) {
    if (this.sessionId && this.sessionId !== sessionId) {
      this.flushBeforeLeaving();
    }

    this.sessionId = sessionId;
  }

  record(type: PlaybackEvent['type'], fields: Omit<PlaybackEvent, 'event_id' | 'seq' | 'type' | 'at'> = {}) {
    if (!this.sessionId) {
      return;
    }

    this.queue.push({ event_id: uuid(), seq: nextEventSeq(), type, at: Date.now(), ...fields });

    if (this.queue.length > MAX_QUEUE) {
      this.queue.splice(0, this.queue.length - MAX_QUEUE);
    }

    if (this.queue.length >= BATCH) {
      void this.flush();
    }
    else if (this.timer === null) {
      this.timer = window.setTimeout(() => {
        this.timer = null;
        void this.flush();
      }, FLUSH_MS);
    }
  }

  async flush() {
    if (!this.sessionId || this.sending || this.queue.length === 0) {
      return;
    }

    const sessionId = this.sessionId;
    const batch = this.queue.splice(0, MAX_BATCH);

    this.sending = true;

    try {
      await sendEvents(sessionId, batch);
    } catch {
      if (this.sessionId === sessionId) {
        this.queue.unshift(...batch);
      }
    } finally {
      this.sending = false;
    }
  }

  flushBeforeLeaving() {
    if (this.timer !== null) {
      window.clearTimeout(this.timer);
      this.timer = null;
    }

    if (this.sessionId && this.queue.length > 0) {
      sendEventsBeforeLeaving(this.sessionId, this.queue.splice(0, MAX_BATCH));
    }

    this.queue = [];
  }
}
