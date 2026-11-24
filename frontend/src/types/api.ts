/** Mirrors backend/app/schemas/response_schema.py */

export interface DependencyStatus {
  name: string;
  status: "ok" | "unavailable" | "degraded";
  url: string | null;
  latency_ms: number | null;
  detail: string | null;
  info: AIHealth | null;
}

export interface HealthResponse {
  service: string;
  status: "ok" | "degraded";
  version: string;
  uptime_seconds: number;
  process_memory_mb: number;
  process_cpu_percent: number;
  dependencies: DependencyStatus[];
}

export interface AIComponentHealth {
  enabled: boolean;
  provider: string;
  loaded: boolean;
  package?: { installed: boolean; version: string | null };
  credentials_configured?: boolean;
  configured?: boolean;
}

export interface AIHealth {
  service: string;
  status: string;
  process_memory_mb: number;
  components: Record<string, AIComponentHealth>;
}

export interface ApiErrorBody {
  error_code: string;
  message: string;
  details?: Record<string, unknown>;
  request_id?: string | null;
}

/** Backend clip processing states (Phase 5 contract). */
export type BackendClipStatus = "received" | "queued" | "processing" | "completed" | "failed";

export interface SessionResponse {
  session_id: string;
  status: "active" | "stopped";
  created_at: string;
  stopped_at?: string | null;
}

export interface ClipStatusResponse {
  clip_id: string;
  session_id: string;
  sequence_number: number;
  status: BackendClipStatus;
  /** SHA-256 of the bytes the backend stored, to prove it received the same audio. */
  sha256: string | null;
  size_bytes: number;
  error: string | null;
}

export interface ClipUploadResponse extends ClipStatusResponse {
  /** Present when the backend processed the clip synchronously. */
  result: import("./analysis").AnalysisResult | null;
}
