import type { ExamState, DeviceState } from "../hooks/useExam";

const deviceText: Record<DeviceState, string> = {
  off: "off", requesting: "asking permission…", on: "on", denied: "permission denied", unavailable: "not available", disconnected: "disconnected",
};
const tone = (s: DeviceState) => (s === "on" ? "ok" : s === "off" || s === "requesting" ? "neutral" : "error");

const streamText = {
  off: ["Live updates: off", "neutral"], connecting: ["Live updates: connecting…", "busy"], live: ["Live updates: connected", "ok"],
  reconnecting: ["Live updates: reconnecting…", "warn"], polling: ["Live updates unavailable - polling", "warn"], closed: ["Live updates: closed", "neutral"],
} as const;

export function StatusBar({ state }: { state: ExamState }) {
  const [streamLabel, streamTone] = streamText[state.stream];
  const vadLabel = { off: "Speech detection: off", loading: "Speech detection: loading model…", ready: "Speech detection: active", error: "Speech detection: failed" }[state.vad];
  return (
    <div className="badges">
      <span className={`badge ${tone(state.camera)}`}>Camera: {deviceText[state.camera]}</span>
      <span className={`badge ${tone(state.microphone)}`}>Microphone: {deviceText[state.microphone]}</span>
      <span className={`badge ${state.vad === "ready" ? "ok" : state.vad === "error" ? "error" : "neutral"}`}>{vadLabel}</span>
      <span className={`badge ${streamTone}`}>{streamLabel}</span>
      {state.tabHidden && state.phase === "running" && <span className="badge warn">Tab in background - monitoring continues</span>}
    </div>
  );
}

export function MicActivity({ state }: { state: ExamState }) {
  const active = state.phase === "running" && state.microphone === "on";
  const levelPercent = active ? Math.max(0, Math.min(100, ((state.level.rmsDbfs + 70) / 60) * 100)) : 0;
  const recordingFor = state.currentEventStartedAt ? Math.max(0, (Date.now() - state.currentEventStartedAt) / 1000) : null;
  return (
    <div className="mic">
      <div className="meter" aria-label="Microphone level">
        <div className="meter-fill" style={{ width: `${levelPercent}%` }} />
      </div>
      <div className={`speech ${state.speaking ? "on" : ""}`}>
        {!active ? "Microphone not monitored"
          : state.detectorState === "RECORDING" ? "● Recording speech event"
          : state.detectorState === "SPEECH_DETECTED" ? "Speech detected…"
          : "Listening - waiting for speech"}
      </div>
      {active && <div className="small">Speech probability {state.level.probability.toFixed(2)}{recordingFor !== null && ` · event running ${recordingFor.toFixed(1)} s`}</div>}
      {active && (
        <div className="small">
          Last 5 s: loudest {state.recent.peakDbfs.toFixed(0)} dBFS, highest speech probability {state.recent.peakProbability.toFixed(2)}
          {state.recent.peakDbfs < -50 && " - very quiet: is this the microphone you are speaking into?"}
        </div>
      )}
      {state.micLabel && <div className="small">Microphone in use: {state.micLabel}{state.diag.micMuted ? " (muted by the system!)" : ""}</div>}
      {state.phase !== "idle" && (
        <div className="small diag">
          Audio analysed: {state.diag.audioSeconds.toFixed(0)} s · events detected {state.diag.detected} · saved {state.diag.saved} · uploaded {state.diag.uploaded}
          {state.diag.contextState !== "running" && active ? ` · audio ${state.diag.contextState}` : ""}
        </div>
      )}
    </div>
  );
}
