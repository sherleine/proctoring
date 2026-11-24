import { useEffect, useRef } from "react";
import type { MediaDeviceState } from "../../hooks/useMediaDevice";
import type { MonitorPhase } from "../../types/proctoring";
import { RecordingTimer } from "./RecordingTimer";

interface Props {
  camera: MediaDeviceState;
  phase: MonitorPhase;
  monitoringSince: number | null;
  capturingSince: number | null;
  onEnableCamera: () => void;
}

/** Live self-view (mirrored for the candidate; saved clips are not mirrored). */
export function WebcamPreview({ camera, phase, monitoringSince, capturingSince, onEnableCamera }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    const el = videoRef.current;
    if (!el) return;
    el.srcObject = camera.stream;
    if (camera.stream) void el.play().catch(() => undefined);
  }, [camera.stream]);

  const hasVideo = camera.status === "active" && camera.stream;
  const capturing = phase === "monitoring" && capturingSince !== null;

  return (
    <div className={`preview ${capturing ? "is-recording" : phase === "monitoring" ? "is-monitoring" : ""}`}>
      <video ref={videoRef} className="preview-video" autoPlay playsInline muted aria-label="Your camera" hidden={!hasVideo} />
      {!hasVideo && (
        <div className="preview-empty">
          <svg viewBox="0 0 24 24" width="40" height="40" aria-hidden>
            <path fill="currentColor" d="M17 10.5V7a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-3.5l4 4v-11l-4 4Z" />
          </svg>
          <p>{camera.status === "requesting" ? "Waiting for camera permission..." : camera.error ?? "Camera is off"}</p>
          {camera.status !== "requesting" && (
            <button className="btn btn-secondary" onClick={onEnableCamera}>
              Enable camera
            </button>
          )}
        </div>
      )}
      {phase === "monitoring" && monitoringSince && (
        <div className="badge-row" role="status" aria-live="polite">
          <span className="mon-badge">
            <span className="mon-dot" aria-hidden />
            MONITORING <RecordingTimer startedAt={monitoringSince} />
          </span>
          {capturing && (
            <span className="rec-badge">
              <span className="rec-dot" aria-hidden />
              REC event <RecordingTimer startedAt={capturingSince!} />
            </span>
          )}
        </div>
      )}
      {phase === "stopping" && (
        <div className="badge-row">
          <span className="mon-badge">Stopping...</span>
        </div>
      )}
    </div>
  );
}
