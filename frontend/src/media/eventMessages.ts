import type { ClipRecorderConfig } from "../audio/clipRecorder";

export interface EventWorkerConfig {
  /** Event timing (speech start/end, merge window, min speech, max length) at 16 kHz. */
  recorder: ClipRecorderConfig;
  modelUrl: string;
  ortBaseUrl: string;
  noiseAlpha: number;
  video: { bitrate: number; frameRate: number; keyframeIntervalUs: number };
  audio: { bitrate: number };
}

export interface EventClipMeta {
  eventId: string;
  partIndex: number;
  splitReason: "speech_end" | "max_duration" | "session_stopped";
  /** Wall-clock ms of the clip's first frame (includes pre-event footage). */
  startedAt: number;
  endedAt: number;
  /** Wall-clock ms when speech was detected (for part 0). */
  speechStartedAt: number;
  durationSec: number;
  preEventSec: number;
  video: { codec: string; width: number; height: number; packets: number };
  audio: { codec: string; sampleRate: number; durationSec: number };
}

export type EventWorkerInbound =
  | { type: "init"; config: EventWorkerConfig; video: ReadableStream<VideoFrame>; audio: ReadableStream<AudioData> }
  | { type: "stop" };

export type EventWorkerOutbound =
  | { type: "ready"; videoCodec: string; audioSampleRate: number }
  | { type: "capture-start"; eventId: string; at: number }
  | { type: "capture-end"; eventId: string; at: number }
  | { type: "misfire"; speechMs: number }
  | { type: "clip"; meta: EventClipMeta; webm: ArrayBuffer; wav: Blob }
  | { type: "error"; stage: "init" | "video" | "audio" | "vad" | "clip"; message: string; fatal: boolean }
  | { type: "stopped" };
