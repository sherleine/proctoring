/**
 * Frontend configuration. Values come from the repository-root .env file
 * (see vite.config.ts `envPrefix`), so the audio contract is defined once and
 * shared with the backend and AI service.
 */
const env = import.meta.env;

function num(value: string | undefined, fallback: number): number {
  if (value === undefined || value.trim() === "") return fallback;
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
}

function bool(value: string | undefined, fallback: boolean): boolean {
  if (value === undefined || value.trim() === "") return fallback;
  return ["1", "true", "yes", "on"].includes(value.trim().toLowerCase());
}

export const appConfig = {
  /** Empty = same origin; Vite proxies /api to the backend in development. */
  apiBaseUrl: (env.VITE_API_BASE_URL ?? "").replace(/\/$/, ""),
  autoStart: bool(env.VITE_AUTO_START, true),
  healthPollMs: num(env.VITE_HEALTH_POLL_MS, 5000),
} as const;

export const audioConfig = {
  sampleRate: num(env.AUDIO_SAMPLE_RATE, 16000),
  channels: num(env.AUDIO_CHANNELS, 1),
  /**
   * Silero VAD v5/v6 accepts exactly 512 samples per call at 16 kHz (32 ms).
   * This is the model's fixed window, slightly above the 20-30 ms guideline.
   */
  frameSamples: 512,
  constraints: {
    echoCancellation: bool(env.VITE_ECHO_CANCELLATION, true),
    noiseSuppression: bool(env.VITE_NOISE_SUPPRESSION, true),
    autoGainControl: bool(env.VITE_AUTO_GAIN_CONTROL, true),
  },
} as const;

export const videoConfig = {
  width: num(env.VITE_VIDEO_WIDTH, 1280),
  height: num(env.VITE_VIDEO_HEIGHT, 720),
  frameRate: num(env.VITE_VIDEO_FRAME_RATE, 30),
} as const;

/** Event-based recording: what counts as an event and how much footage is kept around it. */
export const eventConfig = {
  modelUrl: `/models/silero_vad_${env.VITE_VAD_MODEL === "v5" ? "v5" : "v6"}.onnx`,
  noiseAlpha: num(env.VITE_NOISE_FLOOR_ALPHA, 0.05),
  recorder: {
    sampleRate: 16000,
    frameSamples: 512, // Silero window at 16 kHz
    startThreshold: num(env.VITE_VAD_START_THRESHOLD, 0.5),
    endThreshold: num(env.VITE_VAD_END_THRESHOLD, 0.35),
    /** Sounds with less confident speech than this are ignored (no clip). */
    minSpeechMs: num(env.VITE_VAD_MIN_SPEECH_MS, 250),
    /** Silence that ends an event; shorter pauses are merged into the same event. */
    silenceMs: num(env.EVENT_SILENCE_MS, 1500),
    /** Footage kept before speech was detected. */
    preSpeechMs: num(env.PRE_SPEECH_BUFFER, 2) * 1000,
    /** Footage kept after the last speech. */
    postSpeechPadMs: num(env.EVENT_POST_SPEECH_MS, 1000),
    /** Longer speech is split into consecutive clips of at most this length. */
    maxClipMs: num(env.EVENT_MAX_DURATION, 60) * 1000,
    minStartSnrDb: num(env.VITE_VAD_MIN_START_SNR_DB, 3),
  },
  video: {
    bitrate: num(env.VITE_VIDEO_BITRATE, 1_500_000),
    frameRate: num(env.VITE_VIDEO_FRAME_RATE, 30),
    /** A keyframe every second bounds how far before the event a clip may start. */
    keyframeIntervalUs: 1_000_000,
  },
  audio: { bitrate: 96_000 },
} as const;
