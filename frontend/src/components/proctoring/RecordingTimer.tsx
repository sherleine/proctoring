import { useEffect, useState } from "react";
import { formatClock } from "../../utils/format";

export function RecordingTimer({ startedAt }: { startedAt: number }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 250);
    return () => clearInterval(id);
  }, []);
  return <span className="timer">{formatClock((now - startedAt) / 1000)}</span>;
}
