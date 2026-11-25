import { useEffect, useMemo, useState } from "react";
import { describe } from "../recording/eventStatus";
import type { LocalEvent } from "../storage/eventStore";
import { formatClock } from "../utils/time";
import { ObservationList } from "./ObservationList";

function useObjectUrl(blob: Blob | null): string | null {
  const url = useMemo(() => (blob ? URL.createObjectURL(blob) : null), [blob]);
  useEffect(() => () => { if (url) URL.revokeObjectURL(url); }, [url]);
  return url;
}

function EventRow({ event, now, onRetry }: { event: LocalEvent; now: number; onRetry: (id: string) => void }) {
  const [showVideo, setShowVideo] = useState(false);
  const info = describe(event, now);
  const audioUrl = useObjectUrl(event.wav);
  const videoUrl = useObjectUrl(showVideo ? event.video : null);
  const transcript = event.result?.transcription;
  return (
    <li className="event">
      <div className="event-head">
        <span className="time">Event {event.sequenceNumber + 1}</span>
        <span className="small">{formatClock(event.startedAt)} · {event.durationSeconds.toFixed(1)} s · speech {event.speechSeconds.toFixed(1)} s{event.partIndex > 0 ? ` · part ${event.partIndex + 1}` : ""}</span>
        <span className={`badge ${info.tone}`}>{info.label}</span>
        {info.analysis && <span className="small">({info.analysis})</span>}
        {(info.stage === "upload-failed" || info.stage === "retry-pending") && <button onClick={() => onRetry(event.eventId)}>Retry upload</button>}
      </div>
      {info.detail && <div className="small detail">{info.detail}</div>}
      <div className="playback">
        {audioUrl ? <audio src={audioUrl} controls preload="none" /> : <span className="small">Audio no longer kept in this browser</span>}
        {event.video && <button className="link" onClick={() => setShowVideo((v) => !v)}>{showVideo ? "Hide video" : "Show video"}</button>}
      </div>
      {videoUrl && <video className="clip" src={videoUrl} controls />}
      <ObservationList result={event.result} eventStartedAt={event.startedAt} />
      {transcript?.available && transcript.text && <blockquote className="transcript">“{transcript.text}”</blockquote>}
    </li>
  );
}

export function EventList({ events, now, onRetry }: { events: LocalEvent[]; now: number; onRetry: (id: string) => void }) {
  if (events.length === 0) return <p className="small">No speech events yet. They are created automatically whenever speech is detected.</p>;
  return <ul className="events">{[...events].reverse().map((e) => <EventRow key={e.eventId} event={e} now={now} onRetry={onRetry} />)}</ul>;
}
