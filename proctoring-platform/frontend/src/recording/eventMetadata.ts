import type { DetectedEvent } from "../audio/eventDetector";
import { validateWav } from "../audio/wav";
import type { LocalEvent } from "../storage/eventStore";
import type { EventMetadata } from "../types/contracts";
import { iso } from "../utils/time";
import type { VideoClip } from "../video/videoClipRecorder";

export interface MetadataInput {
  event: Omit<DetectedEvent, "samples">;
  wav: ArrayBuffer;
  sessionId: string;
  sequenceNumber: number;
  epochOfSample: (sample: number) => number;
  vad: { startThreshold: number; endThreshold: number; modelName: string };
  video: VideoClip | null;
  eventId?: string;
  nowMs?: number;
}

export interface EnrolmentInput {
  wav: ArrayBuffer;
  sessionId: string;
  sequenceNumber: number;
  startMs: number;
  speechSeconds: number;
  maxProbability: number;
  vad: { startThreshold: number; endThreshold: number; modelName: string };
  eventId?: string;
  nowMs?: number;
}

export async function buildEnrolmentRecord(input: EnrolmentInput): Promise<LocalEvent> {
  const info = validateWav(input.wav);
  const durationSeconds = Math.round(info.durationSeconds * 1000) / 1000;
  const endMs = input.startMs + durationSeconds * 1000;
  const eventId = input.eventId ?? crypto.randomUUID();
  const groupId = crypto.randomUUID();
  const metadata: EventMetadata = {
    schemaVersion: "1.0",
    eventId,
    eventType: "enrolment",
    sequenceNumber: input.sequenceNumber,
    part: { groupId, index: 0, isFinal: true, overlapSeconds: 0 },
    startedAt: iso(input.startMs),
    endedAt: iso(endMs),
    speechStartedAt: iso(input.startMs),
    durationSeconds,
    preEventSeconds: 0,
    trigger: {
      type: "enrolment",
      model: input.vad.modelName,
      startThreshold: input.vad.startThreshold,
      endThreshold: input.vad.endThreshold,
      maxProbability: Math.min(1, Math.max(0, input.maxProbability)),
      speechSeconds: Math.min(durationSeconds, input.speechSeconds),
      noiseFloorDbfs: null,
    },
    audio: { format: "wav", codec: "pcm_s16le", sampleRate: 16000, channels: 1, sizeBytes: input.wav.byteLength, sha256: await sha256Hex(input.wav) },
    video: { recordedLocally: false, uploaded: false },
  };
  return {
    eventId, kind: "enrolment", sessionId: input.sessionId, sequenceNumber: input.sequenceNumber, groupId, partIndex: 0,
    isFinal: true, startedAt: metadata.startedAt, endedAt: metadata.endedAt, durationSeconds,
    speechSeconds: metadata.trigger.speechSeconds, metadataJson: JSON.stringify(metadata),
    wav: new Blob([input.wav], { type: "audio/wav" }), wavBytes: input.wav.byteLength, video: null, videoMimeType: null,
    videoStartedAt: null, upload: "pending", uploadAttempts: 0, nextAttemptAt: 0, lastError: null, status: null,
    statusUpdatedAt: null, result: null, backendError: null, createdAtMs: input.nowMs ?? Date.now(),
  };
}

export async function sha256Hex(data: ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", data);
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
}

export async function buildEventRecord(input: MetadataInput): Promise<LocalEvent> {
  const { event, wav } = input;
  const info = validateWav(wav); 
  const startMs = input.epochOfSample(event.startSample);
  const durationSeconds = Math.round(info.durationSeconds * 1000) / 1000;
  const endMs = startMs + durationSeconds * 1000;
  const speechMs = Math.min(endMs, Math.max(startMs, input.epochOfSample(event.speechStartSample)));
  const eventId = input.eventId ?? crypto.randomUUID();

  const metadata: EventMetadata = {
    schemaVersion: "1.0",
    eventId,
    eventType: "speech",
    sequenceNumber: input.sequenceNumber,
    part: { groupId: event.groupId, index: event.partIndex, isFinal: event.isFinal, overlapSeconds: event.overlapSeconds },
    startedAt: iso(startMs),
    endedAt: iso(endMs),
    speechStartedAt: iso(speechMs),
    durationSeconds,
    preEventSeconds: event.preEventSeconds,
    trigger: {
      type: "vad",
      model: input.vad.modelName,
      startThreshold: input.vad.startThreshold,
      endThreshold: input.vad.endThreshold,
      maxProbability: Math.min(1, Math.max(0, event.maxProbability)),
      speechSeconds: event.speechSeconds,
      noiseFloorDbfs: event.noiseFloorDbfs,
    },
    audio: { format: "wav", codec: "pcm_s16le", sampleRate: 16000, channels: 1, sizeBytes: wav.byteLength, sha256: await sha256Hex(wav) },
    video: input.video
      ? { recordedLocally: true, uploaded: false, format: "webm", codecs: input.video.mimeType.split("codecs=")[1] ?? "vp8", sizeBytes: input.video.blob.size }
      : { recordedLocally: false, uploaded: false },
  };

  return {
    eventId,
    sessionId: input.sessionId,
    sequenceNumber: input.sequenceNumber,
    groupId: event.groupId,
    partIndex: event.partIndex,
    isFinal: event.isFinal,
    startedAt: metadata.startedAt,
    endedAt: metadata.endedAt,
    durationSeconds,
    speechSeconds: event.speechSeconds,
    metadataJson: JSON.stringify(metadata),
    wav: new Blob([wav], { type: "audio/wav" }),
    wavBytes: wav.byteLength,
    video: input.video?.blob ?? null,
    videoMimeType: input.video?.mimeType ?? null,
    videoStartedAt: input.video ? iso(input.video.startedAtEpochMs) : null,
    upload: "pending",
    uploadAttempts: 0,
    nextAttemptAt: 0,
    lastError: null,
    status: null,
    statusUpdatedAt: null,
    result: null,
    backendError: null,
    createdAtMs: input.nowMs ?? Date.now(),
  };
}
