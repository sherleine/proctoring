import { useEffect, useState } from "react";
import type { CapturedSpan } from "../../hooks/useEventMonitoring";
import { formatClock } from "../../utils/format";

interface Props {
  since: number;
  spans: CapturedSpan[];
  capturingSince: number | null;
}

/** Grey = monitored, not saved. Green = saved event clips. Red = event being recorded now. */
export function SessionTimeline({ since, spans, capturingSince }: Props) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(id);
  }, []);
  const total = Math.max(1, now - since);
  const pos = (t: number) => `${Math.max(0, Math.min(100, ((t - since) / total) * 100))}%`;
  const width = (a: number, b: number) => `${Math.max(0.4, ((Math.min(b, now) - Math.max(a, since)) / total) * 100)}%`;
  const savedSec = spans.reduce((s, x) => s + (x.end - x.start) / 1000, 0);

  return (
    <div className="timeline">
      <div className="timeline-bar" aria-hidden>
        {spans.map((s, i) => (
          <span key={i} className="tl-saved" style={{ left: pos(s.start), width: width(s.start, s.end) }} />
        ))}
        {capturingSince && <span className="tl-live" style={{ left: pos(capturingSince - 2000), width: width(capturingSince - 2000, now) }} />}
      </div>
      <div className="timeline-legend">
        <span>{formatClock(0)}</span>
        <span>
          {spans.length} event{spans.length === 1 ? "" : "s"} saved ({savedSec.toFixed(1)} s of {formatClock(total / 1000)} monitored)
        </span>
        <span>{formatClock(total / 1000)}</span>
      </div>
    </div>
  );
}
