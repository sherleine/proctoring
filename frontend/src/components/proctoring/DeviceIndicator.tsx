import type { MediaDeviceState } from "../../hooks/useMediaDevice";

const ICON = {
  camera: "M17 10.5V7a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-3.5l4 4v-11l-4 4Z",
  microphone: "M12 15a3 3 0 0 0 3-3V6a3 3 0 1 0-6 0v6a3 3 0 0 0 3 3Zm5-3a5 5 0 0 1-10 0H5a7 7 0 0 0 6 6.93V21h2v-2.07A7 7 0 0 0 19 12h-2Z",
};

function describe(d: MediaDeviceState): { tone: "ok" | "warn" | "bad" | "off"; text: string } {
  switch (d.status) {
    case "active":
      return d.muted ? { tone: "warn", text: "Muted by system" } : { tone: "ok", text: "Connected" };
    case "requesting":
      return { tone: "warn", text: "Waiting for permission" };
    case "denied":
      return { tone: "bad", text: "Permission denied" };
    case "unavailable":
      return { tone: "bad", text: "Not found" };
    case "in_use":
      return { tone: "bad", text: "In use by another app" };
    case "disconnected":
      return { tone: "bad", text: "Disconnected" };
    case "unsupported":
    case "error":
      return { tone: "bad", text: "Unavailable" };
    default:
      return { tone: "off", text: "Off" };
  }
}

interface Props {
  kind: "camera" | "microphone";
  device: MediaDeviceState;
  onEnable: () => void;
}

export function DeviceIndicator({ kind, device, onEnable }: Props) {
  const { tone, text } = describe(device);
  const name = kind === "camera" ? "Camera" : "Microphone";
  return (
    <div className={`device device-${tone}`} title={device.error ?? device.label ?? undefined}>
      <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden>
        <path fill="currentColor" d={ICON[kind]} />
      </svg>
      <div className="device-text">
        <span className="device-name">{name}</span>
        <span className="device-state">{text}</span>
      </div>
      {tone === "bad" || device.status === "idle" || device.status === "stopped" ? (
        <button className="btn-link" onClick={onEnable}>
          {device.status === "denied" ? "Retry" : "Enable"}
        </button>
      ) : null}
    </div>
  );
}
