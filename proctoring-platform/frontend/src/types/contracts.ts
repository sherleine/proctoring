// Types of the backend's HTTP contract (docs/contracts/http-api.v1.md and the JSON Schemas).

export interface RecordingPolicy {
  preEventSeconds: number;
  silenceSeconds: number;
  maxEventSeconds: number;
  splitOverlapSeconds: number;
  uploadVideo: boolean;
  maxUploadBytes: number;
}

/** session.v1.json */
export interface Session {
  sessionId: string;
  sessionToken?: string; // only in the create response
  candidateId: string;
  examId: string;
  status: "active" | "ended";
  createdAt: string;
  endedAt: string | null;
  expiresAt?: string;
  endReason?: string | null;
  recordingPolicy: RecordingPolicy;
}

export type EventStatus = "RECEIVED" | "QUEUED" | "PROCESSING" | "COMPLETED" | "FAILED" | "DEAD_LETTERED";

export interface Span {
  start: number;
  end: number;
}

export interface Finding {
  type: string;
  severity: "info" | "review";
  description: string;
  start: number | null;
  end: number | null;
  evidence: Record<string, unknown>;
}

/** analysis-result.v1.json (the parts the UI shows) */
export interface AnalysisResult {
  status: "completed" | "failed";
  processedAt: string;
  processingMs: number;
  models: Record<string, string | null>;
  audio: { durationSeconds: number; rmsDbfs: number; peakDbfs: number } | null;
  speech: { available: boolean; reason?: string | null; verified?: boolean; speechSeconds?: number; segments?: Span[] };
  transcription: {
    available: boolean;
    reason?: string | null;
    language?: string | null;
    languageProbability?: number | null;
    text?: string;
    segments?: (Span & { text: string })[];
  };
  diarization: { available: boolean; reason?: string | null };
  findings: Finding[];
  error: { code: string; message: string } | null;
}

/** event-status.v1.json */
export interface EventStatusResponse {
  eventId: string;
  sessionId: string;
  sequenceNumber: number;
  status: EventStatus;
  receivedAt: string;
  updatedAt: string;
  startedAt: string;
  endedAt?: string;
  durationSeconds: number;
  attempts: number;
  error: { code: string; message: string } | null;
  result: AnalysisResult | null;
}

/** audio-event-metadata.v1.json */
export interface EventMetadata {
  schemaVersion: "1.0";
  eventId: string;
  eventType: "speech";
  sequenceNumber: number;
  part: { groupId: string; index: number; isFinal: boolean; overlapSeconds: number };
  startedAt: string;
  endedAt: string;
  speechStartedAt: string;
  durationSeconds: number;
  preEventSeconds: number;
  trigger: {
    type: "vad";
    model: string;
    startThreshold: number;
    endThreshold: number;
    maxProbability: number;
    speechSeconds: number;
    noiseFloorDbfs: number | null;
  };
  audio: { format: "wav"; codec: "pcm_s16le"; sampleRate: 16000; channels: 1; sizeBytes: number; sha256: string };
  video: { recordedLocally: boolean; uploaded: false; format?: "webm"; codecs?: string; sizeBytes?: number };
}

/** RFC 7807 problem body returned by the backend for every error. */
export interface Problem {
  type: string;
  title?: string;
  status: number;
  detail?: string;
  errors?: Record<string, string[]>;
  correlationId?: string;
  event?: EventStatusResponse; // 503 on upload: stored, will be queued automatically
}
