import type { EventView } from "../../hooks/useSavedEvents";
import { formatBytes, formatTime } from "../../utils/format";

interface Props {
  view: EventView;
  onDelete: (key: string) => void;
  /** Load media metadata/first frame immediately (newest cards); older cards load on play. */
  eager: boolean;
}

export function SavedEventCard({ view, onDelete, eager }: Props) {
  const e = view.event;
  const label = e.partIndex > 0 ? `Event ${e.eventId} (part ${e.partIndex + 1})` : `Event ${e.eventId}`;
  return (
    <article className="saved-card" data-event-key={view.key}>
      <header className="saved-head">
        <div>
          <h3>{label}</h3>
          <p className="saved-times">
            {new Date(e.startedAt).toLocaleDateString()} · {formatTime(e.startedAt)} - {formatTime(e.endedAt)} · {e.durationSec.toFixed(1)} s
            {e.preEventSec > 0 && ` · includes ${e.preEventSec.toFixed(1)} s before speech`}
          </p>
        </div>
        <span className="saved-pill">{e.status}</span>
      </header>

      <div className="saved-media">
        <figure>
          <figcaption>Video with audio</figcaption>
          {/* #t=0.1 makes the browser show the first frame instead of a black box */}
          <video src={`${view.videoUrl}#t=0.1`} controls preload={eager ? "metadata" : "none"} playsInline className="saved-video" />
          <a className="btn btn-secondary" href={view.videoUrl} download={e.video.fileName}>
            Download Video
          </a>
          <span className="file-meta">
            {e.video.fileName} · {formatBytes(e.video.sizeBytes)}
          </span>
        </figure>
        <figure>
          <figcaption>Audio only</figcaption>
          <audio src={view.audioUrl} controls preload={eager ? "metadata" : "none"} className="saved-audio" />
          <a className="btn btn-secondary" href={view.audioUrl} download={e.audio.fileName}>
            Download Audio
          </a>
          <span className="file-meta">
            {e.audio.fileName} · {formatBytes(e.audio.sizeBytes)} · {(e.audio.sampleRate / 1000).toFixed(1)} kHz
          </span>
        </figure>
      </div>

      <footer className="saved-foot">
        <button
          className="btn-link danger"
          onClick={() => {
            if (confirm("Delete this event from this browser? This cannot be undone.")) onDelete(view.key);
          }}
        >
          Delete
        </button>
      </footer>
    </article>
  );
}
