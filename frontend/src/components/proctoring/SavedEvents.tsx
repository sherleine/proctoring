import type { EventView } from "../../hooks/useSavedEvents";
import { SavedEventCard } from "./SavedEventCard";

interface Props {
  items: EventView[];
  loading: boolean;
  error: string | null;
  onDelete: (key: string) => void;
}

export function SavedEvents({ items, loading, error, onDelete }: Props) {
  return (
    <section className="saved" aria-labelledby="saved-title">
      <div className="saved-title-row">
        <h2 id="saved-title">Saved Events</h2>
        <span className="muted">Stored in this browser</span>
      </div>
      {error && <p className="notice notice-error">{error}</p>}
      {loading ? (
        <p className="muted">Loading...</p>
      ) : items.length === 0 ? (
        <p className="saved-empty">No events yet. While monitoring, a short clip is saved each time speech is detected.</p>
      ) : (
        <div className="saved-list">
          {items.map((v, i) => (
            <SavedEventCard key={v.key} view={v} onDelete={onDelete} eager={i < 6} />
          ))}
        </div>
      )}
    </section>
  );
}
