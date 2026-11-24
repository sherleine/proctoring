/**
 * IndexedDB persistence for saved event clips (video + audio Blobs and metadata).
 * Blobs are stored directly; IndexedDB keeps them on disk, not in page memory.
 */
import type { SavedEvent } from "../types/proctoring";

const DB_NAME = "proctoring-recordings";
const DB_VERSION = 2; // v1 held continuous session recordings (store "recordings", left untouched)
const STORE = "events";

let dbPromise: Promise<IDBDatabase> | null = null;

function openDb(): Promise<IDBDatabase> {
  if (!dbPromise) {
    dbPromise = new Promise((resolve, reject) => {
      if (typeof indexedDB === "undefined") {
        reject(new Error("IndexedDB is not available in this browser"));
        return;
      }
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(STORE)) {
          const store = db.createObjectStore(STORE, { keyPath: "key" });
          store.createIndex("startedAt", "startedAt");
        }
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error ?? new Error("Could not open IndexedDB"));
    });
    dbPromise.catch(() => (dbPromise = null));
  }
  return dbPromise;
}

function tx<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return openDb().then(
    (db) =>
      new Promise<T>((resolve, reject) => {
        const t = db.transaction(STORE, mode);
        const req = run(t.objectStore(STORE));
        t.oncomplete = () => resolve(req.result);
        t.onerror = () => reject(t.error ?? req.error ?? new Error("IndexedDB transaction failed"));
        t.onabort = () => reject(t.error ?? new Error("IndexedDB transaction aborted (storage quota?)"));
      }),
  );
}

/** Store key: one entry per clip (event id + part). */
export const eventKey = (e: Pick<SavedEvent, "eventId" | "partIndex">) => `${e.eventId}:${e.partIndex}`;

type Row = SavedEvent & { key: string };

export const recordingStore = {
  async save(event: SavedEvent): Promise<void> {
    await navigator.storage?.persist?.().catch(() => false);
    await tx("readwrite", (s) => s.put({ ...event, key: eventKey(event) } satisfies Row));
  },

  async list(): Promise<SavedEvent[]> {
    const rows = await tx<Row[]>("readonly", (s) => s.getAll());
    return rows.map(({ key: _key, ...e }) => e as SavedEvent).sort((a, b) => b.startedAt - a.startedAt);
  },

  async remove(key: string): Promise<void> {
    await tx("readwrite", (s) => s.delete(key));
  },
};
