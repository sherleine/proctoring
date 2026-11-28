const env = import.meta.env;

function num(value: string | undefined, fallback: number): number {
  const n = Number(value);
  return value !== undefined && value !== "" && Number.isFinite(n) ? n : fallback;
}

export const config = {
  apiBaseUrl: (env.VITE_API_BASE_URL ?? "http://localhost:5080").replace(/\/+$/, ""),
  candidateId: env.VITE_CANDIDATE_ID ?? "cand-demo",
  examId: env.VITE_EXAM_ID ?? "exam-demo",
  vad: {
    startThreshold: num(env.VITE_VAD_START_THRESHOLD, 0.5),
    endThreshold: num(env.VITE_VAD_END_THRESHOLD, 0.35),
    minSpeechMs: num(env.VITE_VAD_MIN_SPEECH_MS, 250),
    tailPadMs: num(env.VITE_VAD_TAIL_PAD_MS, 500),
    modelUrl: "/models/silero_vad_v5.onnx",
    modelName: "silero-vad-v5",
  },
  micAudioProcessing: (env.VITE_MIC_AUDIO_PROCESSING ?? "true").toLowerCase() !== "false",
  localRetentionDays: num(env.VITE_LOCAL_RETENTION_DAYS, 7),
  pollFallbackMs: num(env.VITE_UPDATES_POLL_FALLBACK_MS, 4000),
} as const;
