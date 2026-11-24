import type { MediaDeviceState } from "../../hooks/useMediaDevice";
import type { MonitorPhase } from "../../types/proctoring";
import { DeviceIndicator } from "./DeviceIndicator";

interface Props {
  phase: MonitorPhase;
  capturing: boolean;
  camera: MediaDeviceState;
  mic: MediaDeviceState;
  onEnableCamera: () => void;
  onEnableMic: () => void;
  onStart: () => void;
  onStop: () => void;
}

export function RecordingControls(p: Props) {
  const ready = p.camera.status === "active" && p.mic.status === "active";
  const running = p.phase === "monitoring" || p.phase === "stopping";
  return (
    <div className="controls">
      <div className="devices">
        <DeviceIndicator kind="camera" device={p.camera} onEnable={p.onEnableCamera} />
        <DeviceIndicator kind="microphone" device={p.mic} onEnable={p.onEnableMic} />
      </div>
      <div className="controls-main">
        <span className="controls-hint">
          {p.phase === "monitoring"
            ? p.capturing
              ? "Speech detected - recording this event"
              : "Listening - clips are saved only when speech is detected"
            : ready
              ? "Ready"
              : "Camera and microphone are required"}
        </span>
        {running ? (
          <button className="btn btn-stop" onClick={p.onStop} disabled={p.phase === "stopping"}>
            <span className="stop-square" aria-hidden />
            {p.phase === "stopping" ? "Stopping..." : "Stop Monitoring"}
          </button>
        ) : (
          <button className="btn btn-record" onClick={p.onStart} disabled={p.phase === "starting" || !ready}>
            <span className="record-circle" aria-hidden />
            {p.phase === "starting" ? "Starting..." : "Start Monitoring"}
          </button>
        )}
      </div>
    </div>
  );
}
