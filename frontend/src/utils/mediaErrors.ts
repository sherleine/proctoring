import type { DeviceStatus } from "../types/proctoring";

/** Maps getUserMedia errors to a device status and a user-facing message. */
export function describeMediaError(err: unknown, device: "camera" | "microphone"): { status: DeviceStatus; message: string } {
  const e = err as DOMException;
  const Device = device === "camera" ? "Camera" : "Microphone";
  switch (e?.name) {
    case "NotAllowedError":
    case "SecurityError":
      return { status: "denied", message: `${Device} permission was denied. Allow access in the browser's site settings, then try again.` };
    case "NotFoundError":
    case "OverconstrainedError":
      return { status: "unavailable", message: `No ${device} was found. Connect a ${device} and try again.` };
    case "NotReadableError":
    case "AbortError":
      return { status: "in_use", message: `The ${device} could not be opened. It may be in use by another application.` };
    default:
      return { status: "error", message: `${Device} error: ${e?.message ?? String(err)}` };
  }
}
