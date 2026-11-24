/**
 * Event-based proctoring:
 *   camera + microphone (independent permissions) -> live preview
 *   Start Monitoring -> EventMonitor worker keeps a rolling buffer, nothing is saved
 *   speech detected  -> event captured with the pre-event buffer
 *   event ends       -> event_<id>.webm + event_<id>_audio.wav -> IndexedDB -> card
 * Monitoring continues while clips are being saved.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { appConfig, eventConfig } from "../config/appConfig";
import type { EventClipMeta, EventWorkerOutbound } from "../media/eventMessages";
import { EventMonitor } from "../media/eventMonitor";
import type { MonitorPhase, SavedEvent } from "../types/proctoring";
import { sha256Hex } from "../utils/checksum";
import { useCamera } from "./useCamera";
import { useMicrophone } from "./useMicrophone";
import { useSavedEvents } from "./useSavedEvents";

const shortId = () => crypto.randomUUID().replace(/-/g, "").slice(0, 12);

export interface CapturedSpan {
  start: number;
  end: number;
}

export function useEventMonitoring() {
  const [phase, setPhase] = useState<MonitorPhase>("idle");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [monitoringSince, setMonitoringSince] = useState<number | null>(null);
  const [capturingSince, setCapturingSince] = useState<number | null>(null);
  const [sessionSpans, setSessionSpans] = useState<CapturedSpan[]>([]);
  const [sessionId] = useState(() => shortId());

  const monitorRef = useRef<EventMonitor | null>(null);
  const phaseRef = useRef<MonitorPhase>("idle");
  phaseRef.current = phase;
  const eventIds = useRef(new Map<string, string>()); // VAD event uuid -> short display id

  const saved = useSavedEvents();
  const savedRef = useRef(saved);
  savedRef.current = saved;

  const saveClip = useCallback(
    async (meta: EventClipMeta, webm: ArrayBuffer, wav: Blob) => {
      let id = eventIds.current.get(meta.eventId);
      if (!id) {
        id = shortId();
        eventIds.current.set(meta.eventId, id);
      }
      const base = meta.partIndex > 0 ? `event_${id}_part${meta.partIndex + 1}` : `event_${id}`;
      const video = new Blob([webm], { type: "video/webm" });
      const [videoSha, audioSha] = await Promise.all([sha256Hex(video), sha256Hex(wav)]);
      const event: SavedEvent = {
        schemaVersion: 2,
        eventId: id,
        partIndex: meta.partIndex,
        splitReason: meta.splitReason,
        sessionId,
        startedAt: meta.startedAt,
        endedAt: meta.endedAt,
        speechStartedAt: meta.speechStartedAt,
        durationSec: meta.durationSec,
        preEventSec: meta.preEventSec,
        status: "SAVED",
        video: {
          blob: video,
          fileName: `${base}.webm`,
          mimeType: "video/webm",
          sizeBytes: video.size,
          sha256: videoSha,
          durationSec: meta.durationSec,
          width: meta.video.width,
          height: meta.video.height,
        },
        audio: {
          blob: wav,
          fileName: `${base}_audio.wav`,
          mimeType: "audio/wav",
          sizeBytes: wav.size,
          sha256: audioSha,
          durationSec: meta.audio.durationSec,
          sampleRate: meta.audio.sampleRate,
        },
        upload: null,
      };
      try {
        await savedRef.current.add(event);
        setSessionSpans((s) => [...s, { start: meta.startedAt, end: meta.endedAt }]);
        setNotice(`Event saved: ${event.video.fileName} and ${event.audio.fileName}`);
      } catch (err) {
        setError(`An event could not be saved: ${(err as Error).message}`);
      }
    },
    [sessionId],
  );

  const onWorkerMessage = useCallback(
    (msg: EventWorkerOutbound) => {
      switch (msg.type) {
        case "capture-start":
          setCapturingSince(msg.at);
          break;
        case "capture-end":
          setCapturingSince(null);
          break;
        case "clip":
          void saveClip(msg.meta, msg.webm, msg.wav);
          break;
        case "error":
          if (msg.fatal && phaseRef.current === "monitoring") {
            setError(`Monitoring stopped: ${msg.message}`);
            void stopRef.current();
          } else if (!msg.fatal) {
            setError(msg.message);
          }
          break;
        default:
          break;
      }
    },
    [saveClip],
  );

  const stopRef = useRef<() => Promise<void>>(async () => undefined);
  const onDeviceLost = (message: string) => {
    if (phaseRef.current === "monitoring") {
      setError(`${message} Monitoring stopped; an event in progress was saved.`);
      void stopRef.current();
    }
  };
  const camera = useCamera({ onDisconnected: onDeviceLost });
  const mic = useMicrophone({ onDisconnected: onDeviceLost });

  /**
   * Camera and microphone are requested independently (one failing does not block the
   * other) but one after the other: concurrent requests can race inside the browser.
   */
  const enableDevices = useCallback(async () => {
    await camera.request().catch(() => null);
    await mic.request().catch(() => null);
  }, [camera, mic]);

  const autoRequested = useRef(false);
  useEffect(() => {
    if (appConfig.autoStart && !autoRequested.current) {
      autoRequested.current = true;
      void enableDevices();
    }
  }, [enableDevices]);

  const start = useCallback(async () => {
    if (phaseRef.current !== "idle") return;
    setError(null);
    setNotice(null);
    setPhase("starting");
    try {
      const camStream = await camera.request();
      const micStream = await mic.request();
      const videoTrack = camStream?.getVideoTracks()[0];
      const audioTrack = micStream?.getAudioTracks()[0];
      if (!videoTrack || !audioTrack) {
        setError("Monitoring needs both the camera and the microphone. Enable the device(s) and try again.");
        setPhase("idle");
        return;
      }
      const monitor = new EventMonitor(
        {
          recorder: eventConfig.recorder,
          modelUrl: new URL(eventConfig.modelUrl, location.origin).href,
          ortBaseUrl: new URL("/ort/", location.origin).href,
          noiseAlpha: eventConfig.noiseAlpha,
          video: eventConfig.video,
          audio: eventConfig.audio,
        },
        onWorkerMessage,
      );
      await monitor.start(videoTrack, audioTrack);
      monitorRef.current = monitor;
      setSessionSpans([]);
      setMonitoringSince(Date.now());
      setPhase("monitoring");
    } catch (err) {
      setError(`Monitoring could not start: ${(err as Error).message}`);
      setPhase("idle");
    }
  }, [camera, mic, onWorkerMessage]);

  const stop = useCallback(async () => {
    const monitor = monitorRef.current;
    if (!monitor || phaseRef.current !== "monitoring") return;
    phaseRef.current = "stopping";
    setPhase("stopping");
    monitorRef.current = null;
    try {
      await monitor.stop(); // saves an event in progress
    } finally {
      setCapturingSince(null);
      setMonitoringSince(null);
      setPhase("idle");
    }
  }, []);
  stopRef.current = stop;

  useEffect(() => {
    if (phase !== "monitoring") return;
    const warn = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [phase]);

  useEffect(() => () => void monitorRef.current?.stop(0), []);

  return {
    sessionId,
    phase,
    error,
    dismissError: () => setError(null),
    notice,
    dismissNotice: () => setNotice(null),
    monitoringSince,
    capturingSince,
    sessionSpans,
    camera,
    mic,
    enableDevices,
    start,
    stop,
    saved,
  };
}
