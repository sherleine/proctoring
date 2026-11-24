// IndexedDB persistence of events in this browser: metadata, the WAV until it is uploaded, the local video
// clip, upload state and the backend's status/result. No candidate personal data beyond the session id.
import type { AnalysisResult, EventStatus } from "../types/contracts";

export type UploadState = "pending" | "uploading" | "uploaded" | "retry-pending" | "failed";

export interface LocalEvent {
  eventId: string;
  sessionId: string;
  sequenceNumber: number;
  groupId: string;
  partIndex: number;
  isFinal: boolean;
  startedAt: string;
  endedAt: string;
  durationSeconds: number;
  speechSeconds: number;
  /** Exact JSON sent as the "metadata" part; re-sent unchanged on retries (idempotent upload). */
  metadataJson: string;
  wav: Blob | null; // removed after a successful upload
  wavBytes: number;
  video: Blob | null;
  videoMimeType: string | null;
  videoStartedAt: string | null;
  upload: UploadState;
  uploadAttempts: number;
  nextAttemptAt: number;
  lastError: string | null;
  status: EventStatus | null; // from the backend
  statusUpdatedAt: string | null;
  result: AnalysisResult | null;
  backendError: { code: string; message: string } | null;
  createdAtMs: number;
}

const DB_NAME = "proctoring-poc";
const STORE = "events";

export class EventStore {
  private constructor(private readonly db: IDBDatabase) {}

  static open(factory: IDBFactory = indexedDB): Promise<EventStore> {
    return new Promise((resolve, reject) => {
      const request = factory.open(DB_NAME, 1);
      request.onupgradeneeded = () => {
        const store = request.result.createObjectStore(STORE, { keyPath: "eventId" });
        store.createIndex("sessionId", "sessionId");
      };
      request.onsuccess = () => resolve(new EventStore(request.result));
      request.onerror = () => reject(new Error(`Local storage (IndexedDB) unavailable: ${request.error?.message}`));
    });
  }

  put(event: LocalEvent): Promise<void> {
    return this.tx("readwrite", (s) => s.put(event)).then(() => undefined);
  }

  get(eventId: string): Promise<LocalEvent | undefined> {
    return this.tx("readonly", (s) => s.get(eventId)) as Promise<LocalEvent | undefined>;
  }

  /** Applies a change to a stored event (no-op if it is gone). Returns the new value. */
  async update(eventId: string, change: (e: LocalEvent) => Partial<LocalEvent>): Promise<LocalEvent | undefined> {
    const current = await this.get(eventId);
    if (!current) return undefined;
    const next = { ...current, ...change(current) };
    await this.put(next);
    return next;
  }

  async listBySession(sessionId: string): Promise<LocalEvent[]> {
    const all = (await this.tx("readonly", (s) => s.index("sessionId").getAll(sessionId))) as LocalEvent[];
    return all.sort((a, b) => a.sequenceNumber - b.sequenceNumber);
  }

  async listAll(): Promise<LocalEvent[]> {
    return (await this.tx("readonly", (s) => s.getAll())) as LocalEvent[];
  }

  /** Retention: deletes events created before `cutoffMs`. Returns how many were removed. */
  async deleteOlderThan(cutoffMs: number): Promise<number> {
    const old = (await this.listAll()).filter((e) => e.createdAtMs < cutoffMs);
    for (const e of old) await this.tx("readwrite", (s) => s.delete(e.eventId));
    return old.length;
  }

  close(): void {
    this.db.close();
  }

  private tx(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest): Promise<unknown> {
    return new Promise((resolve, reject) => {
      const transaction = this.db.transaction(STORE, mode);
      const request = run(transaction.objectStore(STORE));
      transaction.oncomplete = () => resolve(request.result);
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error);
    });
  }
}
