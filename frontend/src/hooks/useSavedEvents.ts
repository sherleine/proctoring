/**
 * Saved event clips backed by IndexedDB. Object URLs for playback/download are
 * created per clip and revoked when it is removed or the page unmounts.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { eventKey, recordingStore } from "../media/recordingStore";
import type { SavedEvent } from "../types/proctoring";

export interface EventView {
  key: string;
  event: SavedEvent;
  videoUrl: string;
  audioUrl: string;
}

const toView = (e: SavedEvent): EventView => ({
  key: eventKey(e),
  event: e,
  videoUrl: URL.createObjectURL(e.video.blob),
  audioUrl: URL.createObjectURL(e.audio.blob),
});

const revoke = (v: EventView) => {
  URL.revokeObjectURL(v.videoUrl);
  URL.revokeObjectURL(v.audioUrl);
};

export function useSavedEvents() {
  const [items, setItems] = useState<EventView[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<EventView[]>([]);

  const set = (next: EventView[]) => {
    ref.current = next;
    setItems(next);
  };

  useEffect(() => {
    let cancelled = false;
    recordingStore
      .list()
      .then((list) => !cancelled && set(list.map(toView)))
      .catch((err) => !cancelled && setError(`Saved events could not be loaded: ${(err as Error).message}`))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
      ref.current.forEach(revoke);
      ref.current = [];
    };
  }, []);

  /** Persists first, then shows the card, so a visible card is always really saved. */
  const add = useCallback(async (event: SavedEvent) => {
    await recordingStore.save(event);
    set([toView(event), ...ref.current]);
  }, []);

  const remove = useCallback(async (key: string) => {
    await recordingStore.remove(key);
    const gone = ref.current.find((v) => v.key === key);
    if (gone) revoke(gone);
    set(ref.current.filter((v) => v.key !== key));
  }, []);

  return { items, loading, error, add, remove };
}
