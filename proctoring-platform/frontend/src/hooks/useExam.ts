// Orchestrates one exam: devices -> session -> speech detection -> events -> uploads -> live results.
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiClient, ApiError } from "../api/client";
import { UpdateStream, type StreamState } from "../api/sse";
import { AudioPipeline, type PipelineEvent } from "../audio/audioPipeline";
import { config } from "../config";
import { buildEventRecord } from "../recording/eventMetadata";
import { backendChange } from "../recording/eventStatus";
import { UploadQueue } from "../recording/uploadQueue";
import { EventStore, type LocalEvent } from "../storage/eventStore";
import type { EventStatusResponse, Session } from "../types/contracts";
import { randomKey } from "../utils/time";
import { pickVideoMimeType, VideoClipRecorder } from "../video/videoClipRecorder";

export type Phase = "idle" | "starting" | "running" | "interrupted" | "ending" | "ended";
export type DeviceState = "off" | "requesting" | "on" | "denied" | "unavailable" | "disconnected";

export interface ExamState {
  phase: Phase;
  session: Session | null;
  camera: DeviceState;
  microphone: DeviceState;
  vad: "off" | "loading" | "ready" | "error";
  level: { probability: number; rmsDbfs: number };
  speaking: boolean;
  detectorState: "IDLE" | "SPEECH_DETECTED" | "RECORDING" | "FINALIZING";
  currentEventStartedAt: number | null;
  stream: StreamState | "off";
  tabHidden: boolean;
  /** Microphone in use (browser label), the selectable microphones and the chosen one ("" = default). */
  micLabel: string | null;
  micDevices: { id: string; label: string }[];
  micId: string;
  /** Loudest level and highest speech probability of the last 5 s - for checking the microphone. */
  recent: { peakDbfs: number; peakProbability: number };
  /** Pipeline health, shown on the page (and on window.__proctoringDiag for automated checks). */
  diag: Diagnostics;
  events: LocalEvent[];
  errors: string[];
  notices: string[];
}

export interface Diagnostics {
  audioSeconds: number; // microphone audio analysed by the speech detector so far
  lastFrameAt: number; // wall time of the latest analysed audio (0 = none yet)
  contextState: string;
  micMuted: boolean;
  detected: number; // events produced by the detector
  saved: number; // events stored in IndexedDB
  uploaded: number; // events accepted by the backend
}

const emptyDiag: Diagnostics = { audioSeconds: 0, lastFrameAt: 0, contextState: "closed", micMuted: false, detected: 0, saved: 0, uploaded: 0 };

const SESSION_KEY = "proctoring.session";
const MIC_KEY = "proctoring.microphone";

function savedMic(): string {
  try {
    return localStorage.getItem(MIC_KEY) ?? "";
  } catch {
    return "";
  }
}
const api = new ApiClient(config.apiBaseUrl);

interface StoredSession {
  session: Session;
  token: string;
}

function loadStoredSession(): StoredSession | null {
  try {
    return JSON.parse(sessionStorage.getItem(SESSION_KEY) ?? "null");
  } catch {
    return null;
  }
}

/** The token lives only in this tab's sessionStorage (cleared when the tab closes) and in memory. */
function tokenFor(sessionId: string): string | null {
  const stored = loadStoredSession();
  return stored?.session.sessionId === sessionId ? stored.token : null;
}

const initial: ExamState = {
  phase: "idle", session: null, camera: "off", microphone: "off", vad: "off", level: { probability: 0, rmsDbfs: -100 },
  speaking: false, detectorState: "IDLE", currentEventStartedAt: null, stream: "off", tabHidden: false, events: [], errors: [], notices: [],
  micLabel: null, micDevices: [], micId: "", recent: { peakDbfs: -100, peakProbability: 0 }, diag: emptyDiag,
};

export function useExam() {
  const [state, setState] = useState<ExamState>(() => ({ ...initial, micId: savedMic() }));
  const patch = useCallback((p: Partial<ExamState>) => setState((s) => ({ ...s, ...p })), []);
  const addError = useCallback((message: string) => setState((s) => ({ ...s, errors: [...s.errors.filter((e) => e !== message), message] })), []);
  const setDiag = useCallback((change: Partial<Diagnostics>) => setState((s) => {
    const diag = { ...s.diag, ...change };
    (window as unknown as { __proctoringDiag: Diagnostics }).__proctoringDiag = diag;
    return { ...s, diag };
  }), []);
  const addNotice = useCallback((message: string) => setState((s) => ({ ...s, notices: [...s.notices.slice(-4), message] })), []);

  const refs = useRef({
    store: null as EventStore | null,
    queue: null as UploadQueue | null,
    media: null as MediaStream | null,
    pipeline: null as AudioPipeline | null,
    video: null as VideoClipRecorder | null,
    videoHold: null as object | null,
    updates: null as UpdateStream | null,
    session: null as StoredSession | null,
    sequence: 0,
    pending: new Set<Promise<void>>(),
    levels: [] as { t: number; db: number; p: number }[],
    uploadedIds: new Set<string>(),
    lastFrameAt: 0,
    chosenMic: savedMic(),
  });

  const refreshDevices = useCallback(async () => {
    try {
      const devices = await navigator.mediaDevices.enumerateDevices();
      const mics = devices.filter((d) => d.kind === "audioinput" && d.deviceId !== "default" && d.deviceId !== "communications")
        .map((d, i) => ({ id: d.deviceId, label: d.label || `Microphone ${i + 1}` }));
      patch({ micDevices: mics });
    } catch {
      /* device list is optional */
    }
  }, [patch]);

  const selectMic = useCallback((id: string) => {
    refs.current.chosenMic = id;
    try {
      localStorage.setItem(MIC_KEY, id);
    } catch {
      /* ignore */
    }
    patch({ micId: id });
  }, [patch]);

  const upsert = useCallback((event: LocalEvent) => {
    if (event.upload === "uploaded" && !refs.current.uploadedIds.has(event.eventId)) {
      refs.current.uploadedIds.add(event.eventId);
      setDiag({ uploaded: refs.current.uploadedIds.size });
    }
    setState((s) => {
      const others = s.events.filter((e) => e.eventId !== event.eventId);
      return { ...s, events: [...others, event].sort((a, b) => a.sequenceNumber - b.sequenceNumber) };
    });
  }, [setDiag]);

  // ------------------------------------------------------------------ backend status -> local events

  const applyRemote = useCallback(async (remote: EventStatusResponse) => {
    const store = refs.current.store;
    const local = await store?.get(remote.eventId);
    if (!store || !local) return;
    const change = backendChange(local, remote);
    if (!change) return;
    const next = await store.update(remote.eventId, () => change);
    if (next) upsert(next);
  }, [upsert]);

  const resync = useCallback(async () => {
    const s = refs.current.session;
    if (!s) return;
    try {
      for (const remote of await api.listEvents(s.session.sessionId, s.token)) await applyRemote(remote);
    } catch {
      /* the stream state already shows the connection problem */
    }
  }, [applyRemote]);

  const startUpdates = useCallback((stored: StoredSession) => {
    refs.current.updates?.stop();
    const updates = new UpdateStream(api, stored.session.sessionId, stored.token, {
      onEvent: (e) => void applyRemote(e),
      onResync: () => void resync(),
      onState: (stream) => patch({ stream }),
      onEnd: (reason) => addNotice(reason === "token-expired" ? "Live updates ended: the session token has expired." : "Live updates ended."),
    }, { pollIntervalMs: config.pollFallbackMs });
    refs.current.updates = updates;
    void updates.start();
  }, [addNotice, applyRemote, patch, resync]);

  // ------------------------------------------------------------------ mount: storage, recovery after reload

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const store = await EventStore.open();
        if (cancelled) return;
        refs.current.store = store;
        await store.deleteOlderThan(Date.now() - config.localRetentionDays * 86_400_000);
        refs.current.queue = new UploadQueue(store, api, tokenFor, upsert);
        const stored = loadStoredSession();
        if (stored) {
          refs.current.session = stored;
          const events = await store.listBySession(stored.session.sessionId);
          refs.current.sequence = events.reduce((m, e) => Math.max(m, e.sequenceNumber + 1), 0);
          patch({ session: stored.session, events, phase: stored.session.status === "ended" ? "ended" : "interrupted" });
          startUpdates(stored);
          void refs.current.queue.kick(); // uploads interrupted by the reload
        }
      } catch (err) {
        addError((err as Error).message);
      }
    })();
    const onVisibility = () => patch({ tabHidden: document.visibilityState === "hidden" });
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      cancelled = true;
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [addError, patch, startUpdates, upsert]);

  // ------------------------------------------------------------------ pipeline messages

  const onPipeline = useCallback((msg: PipelineEvent) => {
    const r = refs.current;
    if (msg.type === "level") {
      const now = Date.now();
      r.lastFrameAt = now;
      setDiag({ audioSeconds: msg.samples / 16000, lastFrameAt: now, contextState: r.pipeline?.contextState ?? "closed" });
      r.levels = [...r.levels.filter((l) => now - l.t < 5000), { t: now, db: msg.rmsDbfs, p: msg.probability }];
      patch({
        level: { probability: msg.probability, rmsDbfs: msg.rmsDbfs }, speaking: msg.speaking, detectorState: msg.state,
        recent: { peakDbfs: Math.max(...r.levels.map((l) => l.db)), peakProbability: Math.max(...r.levels.map((l) => l.p)) },
      });
    } else if (msg.type === "speech-start") {
      const clipStartMs = r.pipeline!.epochOfSample(msg.clipStartSample);
      r.videoHold = r.video?.beginClip(clipStartMs) ?? null;
      patch({ speaking: true, detectorState: "RECORDING", currentEventStartedAt: clipStartMs });
    } else if (msg.type === "context-state") {
      setDiag({ contextState: msg.state });
      if (msg.state !== "running" && msg.state !== "closed") addNotice(`Audio processing was ${msg.state} by the browser; resuming.`);
    } else if (msg.type === "event") {
      setState((s) => ({ ...s, diag: { ...s.diag, detected: s.diag.detected + 1 } }));
      const hold = r.videoHold; // taken now: a split's next part may start before this clip is finished
      r.videoHold = null;
      const sequenceNumber = r.sequence++;
      const session = r.session!.session;
      const work = (async () => {
        try {
          // The audio event never waits more than 2 s for its (optional, local-only) video clip.
          const video = await Promise.race([
            r.video?.endClip(hold) ?? Promise.resolve(null),
            new Promise<null>((resolve) => setTimeout(() => resolve(null), 2000)),
          ]).catch(() => null);
          const record = await buildEventRecord({
            event: msg.event, wav: msg.wav, sessionId: session.sessionId, sequenceNumber,
            epochOfSample: (s) => r.pipeline!.epochOfSample(s),
            vad: { startThreshold: config.vad.startThreshold, endThreshold: config.vad.endThreshold, modelName: config.vad.modelName },
            video,
          });
          await r.queue!.enqueue(record);
          setState((s) => ({ ...s, diag: { ...s.diag, saved: s.diag.saved + 1 } }));
        } catch (err) {
          addError(`An event could not be saved: ${(err as Error).message}`);
        }
      })();
      r.pending.add(work);
      void work.finally(() => r.pending.delete(work));
      if (msg.event.isFinal) patch({ currentEventStartedAt: null, speaking: false, detectorState: "IDLE" });
    } else if (msg.type === "error") {
      addError(msg.stage === "wav" ? `Audio clip could not be encoded: ${msg.message}`
        : msg.stage === "pipeline" ? `Audio processing error: ${msg.message}` : `Speech detection stopped working: ${msg.message}`);
      if (msg.stage === "inference") patch({ vad: "error" });
    }
  }, [addError, addNotice, patch, setDiag]);

  // ------------------------------------------------------------------ devices

  const stopCapture = useCallback(async () => {
    const r = refs.current;
    if (r.pipeline) {
      await r.pipeline.flush();
      await Promise.allSettled([...r.pending]);
      await r.pipeline.stop();
    }
    r.video?.stop();
    r.media?.getTracks().forEach((t) => t.stop());
    r.pipeline = null;
    r.video = null;
    r.media = null;
  }, []);

  const watchTracks = useCallback((media: MediaStream) => {
    for (const track of media.getTracks()) {
      if (track.kind === "audio") {
        // "muted" = the browser/OS currently delivers no audio from this microphone (e.g. muted in Windows).
        track.addEventListener("mute", () => { setDiag({ micMuted: true }); addNotice("The microphone stopped delivering audio (muted by the system or another program)."); });
        track.addEventListener("unmute", () => setDiag({ micMuted: false }));
      }
      track.addEventListener("ended", () => {
        if (track.kind === "audio") {
          patch({ microphone: "disconnected", speaking: false });
          addError("The microphone was disconnected. Audio monitoring has stopped - reconnect it and resume the exam.");
          void stopCapture().then(() => patch({ phase: "interrupted", camera: "off", vad: "off" }));
        } else {
          patch({ camera: "disconnected" });
          addError("The camera was disconnected. Audio monitoring continues; video clips are no longer recorded.");
          refs.current.video?.stop();
        }
      });
    }
  }, [addError, addNotice, patch, setDiag, stopCapture]);

  async function openDevices(): Promise<MediaStream> {
    patch({ camera: "requesting", microphone: "requesting" });
    if (!navigator.mediaDevices?.getUserMedia) throw new Error("This browser cannot access the camera and microphone. Use a current Chrome or Edge over http://localhost or https.");
    const processing = config.micAudioProcessing;
    const chosen = refs.current.chosenMic;
    const audio: MediaTrackConstraints = {
      channelCount: 1, echoCancellation: processing, noiseSuppression: processing, autoGainControl: processing,
      ...(chosen ? { deviceId: { exact: chosen } } : {}),
    };
    try {
      const media = await navigator.mediaDevices.getUserMedia({ audio, video: { width: 640, height: 480, frameRate: 15 } });
      patch({ camera: "on", microphone: "on", micLabel: media.getAudioTracks()[0]?.label || "default microphone" });
      void refreshDevices();
      return media;
    } catch (err) {
      const name = (err as DOMException).name;
      if (name === "NotAllowedError" || name === "SecurityError") {
        patch({ camera: "denied", microphone: "denied" });
        throw new Error("Camera and microphone access was denied. Allow both in the browser's site settings (the icon left of the address bar) and try again.");
      }
      // The camera may be missing or busy: continue with audio only, clearly marked.
      try {
        const media = await navigator.mediaDevices.getUserMedia({ audio });
        patch({ camera: "unavailable", microphone: "on", micLabel: media.getAudioTracks()[0]?.label || "default microphone" });
        void refreshDevices();
        addError("No camera could be opened (missing or used by another program). Audio monitoring works; no video clips are recorded.");
        return media;
      } catch (audioErr) {
        const audioName = (audioErr as DOMException).name;
        patch({ camera: "unavailable", microphone: audioName === "NotAllowedError" ? "denied" : "unavailable" });
        if (audioName === "OverconstrainedError" && refs.current.chosenMic) {
          refs.current.chosenMic = "";
          patch({ micId: "" });
          throw new Error("The selected microphone is no longer available. The default microphone is selected now - start again.");
        }
        throw new Error(audioName === "NotAllowedError"
          ? "Microphone access was denied. The exam cannot be monitored without a microphone."
          : "No microphone was found (or it is used by another program). Connect a microphone and try again.");
      }
    }
  }

  // ------------------------------------------------------------------ start / resume / end

  const start = useCallback(async () => {
    const r = refs.current;
    if (!r.store || !r.queue) return addError("Local storage is not available in this browser.");
    patch({ phase: "starting", errors: [], vad: "loading" });
    let devicesOpened = false;
    try {
      const media = await openDevices();
      devicesOpened = true;
      r.media = media;
      watchTracks(media);

      if (!r.session || r.session.session.status === "ended") {
        const key = randomKey();
        try {
          const session = await api.createSession(config.candidateId, config.examId, key);
          r.session = { session, token: session.sessionToken! };
          sessionStorage.setItem(SESSION_KEY, JSON.stringify(r.session));
          r.sequence = 0;
          patch({ session, events: [] });
        } catch (err) {
          throw new Error(err instanceof ApiError && err.status === 0
            ? "The exam server is not reachable. Check that the backend is running and try again."
            : `The exam session could not be created: ${(err as Error).message}`);
        }
        startUpdates(r.session);
      }
      const policy = r.session.session.recordingPolicy;

      const pipeline = new AudioPipeline({
        modelUrl: config.vad.modelUrl,
        detector: {
          sampleRate: 16000, startThreshold: config.vad.startThreshold, endThreshold: config.vad.endThreshold,
          minSpeechMs: config.vad.minSpeechMs, candidateGapMs: 300, silenceMs: policy.silenceSeconds * 1000,
          tailPadMs: Math.min(config.vad.tailPadMs, policy.silenceSeconds * 1000),
          preRollMs: policy.preEventSeconds * 1000, maxEventMs: policy.maxEventSeconds * 1000,
          overlapMs: policy.splitOverlapSeconds * 1000, bufferMs: Math.max(5000, policy.preEventSeconds * 1000),
        },
        onMessage: onPipeline,
      });
      r.pipeline = pipeline;
      try {
        await pipeline.start(media);
      } catch (err) {
        patch({ vad: "error" });
        throw err;
      }
      const mime = pickVideoMimeType();
      if (media.getVideoTracks().length > 0 && mime) {
        r.video = new VideoClipRecorder(media, mime);
        r.video.start();
      }
      patch({ phase: "running", vad: "ready" });
      setDiag({ ...emptyDiag, lastFrameAt: Date.now(), contextState: pipeline.contextState });
      r.lastFrameAt = Date.now();
    } catch (err) {
      await stopCapture();
      patch({ phase: r.session ? "interrupted" : "idle", vad: "off", speaking: false,
        ...(devicesOpened ? { camera: "off" as const, microphone: "off" as const } : {}) });
      addError((err as Error).message);
    }
  }, [addError, onPipeline, patch, startUpdates, stopCapture, watchTracks]);

  const end = useCallback(async () => {
    const r = refs.current;
    patch({ phase: "ending" });
    await stopCapture(); // closes a speech event in progress and stores it
    patch({ camera: "off", microphone: "off", vad: "off", speaking: false, currentEventStartedAt: null, level: { probability: 0, rmsDbfs: -100 } });
    if (r.session) {
      try {
        const session = await api.endSession(r.session.session.sessionId, r.session.token);
        r.session = { ...r.session, session: { ...r.session.session, ...session } };
        sessionStorage.setItem(SESSION_KEY, JSON.stringify(r.session));
        patch({ session: r.session.session });
      } catch (err) {
        addError(`The session could not be ended on the server (${(err as Error).message}). Recorded events are kept and uploaded when possible.`);
      }
    }
    patch({ phase: "ended" });
    // Uploads and live updates continue: results of the last clips still arrive.
  }, [addError, patch, stopCapture]);

  const newExam = useCallback(() => {
    refs.current.updates?.stop();
    refs.current.session = null;
    sessionStorage.removeItem(SESSION_KEY);
    setState({ ...initial });
  }, []);

  const retryUpload = useCallback((eventId: string) => void refs.current.queue?.retry(eventId), []);

  useEffect(() => () => {
    refs.current.updates?.stop();
    refs.current.queue?.stop();
    void stopCapture();
  }, [stopCapture]);

  // Watchdog: during the exam, analysed audio must keep arriving. If it stops, say so and try to resume.
  useEffect(() => {
    if (state.phase !== "running") return;
    let warned = false;
    const timer = setInterval(() => {
      const r = refs.current;
      const silentFor = Date.now() - r.lastFrameAt;
      if (silentFor > 3000) {
        void r.pipeline?.resume();
        if (!warned) addError(`No microphone audio has been analysed for ${Math.round(silentFor / 1000)} s (audio state: ${r.pipeline?.contextState ?? "closed"}). Speech is not being detected right now.`);
        warned = true;
      } else if (warned) {
        warned = false;
        setState((s) => ({ ...s, errors: s.errors.filter((e) => !e.startsWith("No microphone audio has been analysed")) }));
      }
    }, 1000);
    return () => clearInterval(timer);
  }, [state.phase, addError]);

  // Errors that would otherwise only reach the console.
  useEffect(() => {
    const onError = (e: ErrorEvent) => addError(`Unexpected error: ${e.message}`);
    const onRejection = (e: PromiseRejectionEvent) => addError(`Unexpected error: ${(e.reason as Error)?.message ?? String(e.reason)}`);
    window.addEventListener("error", onError);
    window.addEventListener("unhandledrejection", onRejection);
    return () => {
      window.removeEventListener("error", onError);
      window.removeEventListener("unhandledrejection", onRejection);
    };
  }, [addError]);

  useEffect(() => {
    void refreshDevices();
    navigator.mediaDevices?.addEventListener?.("devicechange", refreshDevices);
    return () => navigator.mediaDevices?.removeEventListener?.("devicechange", refreshDevices);
  }, [refreshDevices]);

  return { state, start, end, newExam, retryUpload, selectMic, media: () => refs.current.media };
}
