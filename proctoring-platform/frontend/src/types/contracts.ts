

export interface RecordingPolicy {
  preEventSeconds: number;
  silenceSeconds: number;
  maxEventSeconds: number;
  splitOverlapSeconds: number;
  uploadVideo: boolean;
  maxUploadBytes: number;
}


export interface Session {
  sessionId: string;
  sessionToken?: string; 
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

export type ObservationType =
  | "MULTIPLE_SPEAKERS_DETECTED" | "OVERLAPPING_SPEECH_DETECTED" | "BACKGROUND_CONVERSATION_DETECTED"
  | "NON_CANDIDATE_VOICE" | "POSSIBLE_EXTERNAL_ASSISTANCE" | "SPEECH_DETECTED" | "NO_SPEECH_DETECTED"
  | "PHONE_RINGING" | "KEYBOARD_TYPING" | "WHISPERING" | "UNUSUAL_SOUND"
  
  | "MULTIPLE_SPEAKERS" | "OVERLAPPING_SPEECH" | "BACKGROUND_CONVERSATION";


export interface Observation {
  observationId: string;
  sessionId: string;
  eventId: string;
  observationType: ObservationType;
  startSeconds: number;
  endSeconds: number;
  confidence: number;
  
  confidenceKind: "model-score" | "calibrated-probability" | "rule";
  supportingEvidence: Record<string, unknown>;
  modelName: string;
  modelVersion: string;
  createdAt: string;
  
  category?: "informational" | "review";
}


export interface EnrolmentResult {
  status: "accepted" | "uncertain" | "rejected";
  reason: string | null;
  speechSeconds: number;
  embeddingModel: string;
}

export interface SpeakerComparison {
  start: number;
  end: number;
  similarity: number | null;
  decision: "LIKELY_CANDIDATE" | "LIKELY_NON_CANDIDATE" | "UNCERTAIN";
  reason?: string | null;
}


export interface AnalysisResult {
  schemaVersion?: "1.0" | "1.1";
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
  
  enrolment?: EnrolmentResult | null;
  speaker?: { available: boolean; reason?: string | null; reference?: "present" | "missing"; comparisons?: SpeakerComparison[] };
  observations?: Observation[];
}


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


export interface EventMetadata {
  schemaVersion: "1.0";
  eventId: string;
  eventType: "speech" | "enrolment";
  sequenceNumber: number;
  part: { groupId: string; index: number; isFinal: boolean; overlapSeconds: number };
  startedAt: string;
  endedAt: string;
  speechStartedAt: string;
  durationSeconds: number;
  preEventSeconds: number;
  trigger: {
    type: "vad" | "enrolment";
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


export interface Problem {
  type: string;
  title?: string;
  status: number;
  detail?: string;
  errors?: Record<string, string[]>;
  correlationId?: string;
  event?: EventStatusResponse; 
}
