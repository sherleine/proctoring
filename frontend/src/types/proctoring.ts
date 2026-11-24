export type DeviceStatus =
  | "idle"
  | "requesting"
  | "active"
  | "denied"
  | "unavailable"
  | "in_use"
  | "disconnected"
  | "unsupported"
  | "error"
  | "stopped";

export type MonitorPhase = "idle" | "starting" | "monitoring" | "stopping";

export interface StoredFile {
  blob: Blob;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  /** SHA-256 of the exact bytes stored/downloaded, for future upload verification. */
  sha256: string | null;
  durationSec: number;
}

/**
 * One saved speech event: a short video+audio clip and the matching audio-only WAV.
 * Persisted in IndexedDB. `upload` stays null until a backend upload exists and succeeds.
 */
export interface SavedEvent {
  schemaVersion: 2;
  eventId: string;
  /** Long speech is split at the maximum event length; parts share the same speech event. */
  partIndex: number;
  splitReason: "speech_end" | "max_duration" | "session_stopped";
  sessionId: string;
  /** Wall-clock ms of the clip's first frame (includes the pre-event buffer). */
  startedAt: number;
  endedAt: number;
  /** Wall-clock ms when speech was detected. */
  speechStartedAt: number;
  durationSec: number;
  /** Seconds of footage before speech was detected. */
  preEventSec: number;
  status: "SAVED";
  video: StoredFile & { width: number; height: number };
  audio: StoredFile & { sampleRate: number };
  upload: null;
}
