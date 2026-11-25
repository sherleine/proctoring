
import { ApiError, type ApiClient } from "../api/client";
import type { EventStore, LocalEvent } from "../storage/eventStore";

export interface UploadQueueOptions {
  baseBackoffMs?: number;
  maxBackoffMs?: number;
  now?: () => number;
}

export class UploadQueue {
  private busy = false;
  private again = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private stopped = false;

  constructor(
    private readonly store: EventStore,
    private readonly api: ApiClient,
    private readonly tokenFor: (sessionId: string) => string | null,
    private readonly onChange: (event: LocalEvent) => void,
    private readonly options: UploadQueueOptions = {},
  ) {}

  private now(): number {
    return (this.options.now ?? Date.now)();
  }

  async enqueue(event: LocalEvent): Promise<void> {
    await this.store.put(event);
    this.onChange(event);
    await this.kick();
  }

  
  async retry(eventId: string): Promise<void> {
    const e = await this.store.update(eventId, (x) => (x.upload === "uploaded" ? {} : { upload: "pending", nextAttemptAt: 0, lastError: null }));
    if (e) this.onChange(e);
    await this.kick();
  }

  
  async kick(): Promise<void> {
    if (this.stopped) return;
    if (this.busy) {
      this.again = true;
      return;
    }
    this.busy = true;
    try {
      do {
        this.again = false;
        for (const event of await this.due()) {
          if (this.stopped) return;
          await this.uploadOne(event);
        }
      } while (this.again);
    } finally {
      this.busy = false;
      await this.schedule();
    }
  }

  stop(): void {
    this.stopped = true;
    if (this.timer) clearTimeout(this.timer);
  }

  private async due(): Promise<LocalEvent[]> {
    const now = this.now();
    return (await this.store.listAll())
      .filter((e) => (e.upload === "pending" || e.upload === "uploading" || (e.upload === "retry-pending" && e.nextAttemptAt <= now)) && e.wav)
      .sort((a, b) => a.createdAtMs - b.createdAtMs || a.sequenceNumber - b.sequenceNumber);
  }

  private async schedule(): Promise<void> {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    if (this.stopped) return;
    const waiting = (await this.store.listAll()).filter((e) => e.upload === "retry-pending");
    if (waiting.length === 0) return;
    const next = Math.min(...waiting.map((e) => e.nextAttemptAt));
    this.timer = setTimeout(() => void this.kick(), Math.max(50, next - this.now()));
  }

  private async uploadOne(event: LocalEvent): Promise<void> {
    const token = this.tokenFor(event.sessionId);
    if (!token) {
      await this.save(event.eventId, { upload: "failed", lastError: "No session token in this tab (the session was opened elsewhere)" });
      return;
    }
    await this.save(event.eventId, { upload: "uploading" });
    try {
      const { status, event: backend } = await this.api.uploadEvent(event.sessionId, token, event.metadataJson, event.wav!);
      await this.save(event.eventId, {
        upload: "uploaded",
        lastError: status === 503 ? "Stored by the backend; it will be queued for analysis automatically" : null,
        status: backend.status,
        statusUpdatedAt: backend.updatedAt,
        result: backend.result,
        backendError: backend.error,
      });
    } catch (err) {
      const apiError = err instanceof ApiError ? err : new ApiError(0, (err as Error).message);
      if (apiError.retryable) {
        const attempts = event.uploadAttempts + 1;
        const delay = Math.min(this.options.maxBackoffMs ?? 60_000, (this.options.baseBackoffMs ?? 2000) * 2 ** (attempts - 1));
        await this.save(event.eventId, { upload: "retry-pending", uploadAttempts: attempts, nextAttemptAt: this.now() + delay, lastError: apiError.message });
      } else {
        const reason = apiError.status === 401 || apiError.status === 404 ? "The session is no longer valid" : apiError.message;
        await this.save(event.eventId, { upload: "failed", uploadAttempts: event.uploadAttempts + 1, lastError: reason });
      }
    }
  }

  private async save(eventId: string, change: Partial<LocalEvent>): Promise<void> {
    const e = await this.store.update(eventId, () => change);
    if (e) this.onChange(e);
  }
}
