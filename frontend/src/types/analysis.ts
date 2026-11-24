/** Mirrors ai_service/app/schemas/analysis_schema.py (returned through the backend). */

export interface ComponentStatus {
  available: boolean;
  provider: string | null;
  model: string | null;
  reason: string | null;
}

export interface SpeechRegion {
  start: number;
  end: number;
}

export interface VadAnalysis extends ComponentStatus {
  speech_detected: boolean;
  speech_regions: SpeechRegion[];
  speech_duration_seconds: number;
  speech_ratio: number;
  frontend_speech_ratio: number | null;
  agrees_with_frontend: boolean | null;
}

export interface TranscriptSegment {
  start: number;
  end: number;
  text: string;
  speaker: string | null;
  avg_logprob: number | null;
  no_speech_prob: number | null;
}

export interface Transcription extends ComponentStatus {
  text: string;
  language: string | null;
  language_probability: number | null;
  segments: TranscriptSegment[];
}

export interface SpeakerTurn {
  speaker_id: string;
  start: number;
  end: number;
}

export interface Diarization extends ComponentStatus {
  number_of_speakers: number | null;
  speakers: SpeakerTurn[];
  overlaps: { start: number; end: number; speakers: string[] }[];
  speaker_changes: number | null;
}

export interface AudioClassification extends ComponentStatus {
  top_labels: { label: string; score: number; start: number | null; end: number | null }[];
}

export interface AudioEvent {
  event_type: string;
  description: string;
  start: number | null;
  end: number | null;
  requires_review: boolean;
  source: string;
}

export interface AnalysisResult {
  session_id: string;
  clip_id: string;
  status: "completed" | "failed";
  audio_analysis: {
    speech_detected: boolean;
    number_of_speakers: number | null;
    multiple_speakers_detected: boolean | null;
    overlapping_speech: boolean | null;
  } | null;
  vad: VadAnalysis | null;
  transcription: Transcription | null;
  diarization: Diarization | null;
  audio_classification: AudioClassification | null;
  interpretation: {
    available: boolean;
    observation: string | null;
    requires_review: boolean | null;
    reason: string | null;
  } | null;
  events: AudioEvent[];
  processing_time_ms: number;
  error: string | null;
}
