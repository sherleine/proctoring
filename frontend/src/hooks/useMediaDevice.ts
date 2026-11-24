/**
 * One media input (camera OR microphone): permission, acquisition, mute and
 * disconnection handling. Camera and microphone are requested independently so
 * one being denied does not block the other.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import type { DeviceStatus } from "../types/proctoring";
import { describeMediaError } from "../utils/mediaErrors";

export interface MediaDeviceState {
  status: DeviceStatus;
  error: string | null;
  label: string | null;
  muted: boolean;
  permission: PermissionState | "unknown";
  settings: MediaTrackSettings | null;
  stream: MediaStream | null;
}

interface Options {
  kind: "camera" | "microphone";
  constraints: MediaTrackConstraints;
  /** Fired when an active track ends (unplugged, permission revoked, OS-level stop). */
  onDisconnected?: (reason: string) => void;
}

export function useMediaDevice({ kind, constraints, onDisconnected }: Options) {
  const [state, setState] = useState<MediaDeviceState>({
    status: "idle",
    error: null,
    label: null,
    muted: false,
    permission: "unknown",
    settings: null,
    stream: null,
  });
  const streamRef = useRef<MediaStream | null>(null);
  const onDisconnectedRef = useRef(onDisconnected);
  onDisconnectedRef.current = onDisconnected;
  const constraintsRef = useRef(constraints);
  const Device = kind === "camera" ? "Camera" : "Microphone";

  useEffect(() => {
    let status: PermissionStatus | null = null;
    const update = () => status && setState((s) => ({ ...s, permission: status!.state }));
    navigator.permissions
      ?.query({ name: kind as PermissionName })
      .then((s) => {
        status = s;
        update();
        s.onchange = update;
      })
      .catch(() => undefined);
    return () => {
      if (status) status.onchange = null;
    };
  }, [kind]);

  const requestRef = useRef<() => Promise<MediaStream | null>>(async () => null);
  const reconnectRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleLost = useCallback(
    (reason: string) => {
      const stream = streamRef.current;
      if (!stream) return;
      streamRef.current = null;
      stream.getTracks().forEach((t) => t.stop());
      setState((s) => ({ ...s, status: "disconnected", error: reason, muted: false, stream: null }));
      onDisconnectedRef.current?.(reason);
      // Devices can drop out briefly (USB hiccup, driver reset, another app releasing it):
      // try to reacquire a few times. If that fails, the user can press Enable.
      if (reconnectRef.current) clearTimeout(reconnectRef.current);
      const attempt = (n: number) => {
        reconnectRef.current = setTimeout(async () => {
          reconnectRef.current = null;
          if (streamRef.current) return;
          const stream = await requestRef.current();
          if (!stream && n < 3) attempt(n + 1);
        }, 1000 * 2 ** (n - 1));
      };
      attempt(1);
    },
    [],
  );

  // Detect a physically removed device even if the browser does not end the track.
  useEffect(() => {
    const media = navigator.mediaDevices;
    if (!media?.addEventListener) return;
    const deviceKind = kind === "camera" ? "videoinput" : "audioinput";
    const onChange = async () => {
      const track = streamRef.current?.getTracks()[0];
      const deviceId = track?.getSettings().deviceId;
      if (!track || !deviceId) return;
      const devices = await media.enumerateDevices();
      if (!devices.some((d) => d.kind === deviceKind && d.deviceId === deviceId)) {
        handleLost(`The ${kind} was disconnected.`);
      }
    };
    media.addEventListener("devicechange", onChange);
    return () => media.removeEventListener("devicechange", onChange);
  }, [handleLost, kind]);

  const request = useCallback(async (): Promise<MediaStream | null> => {
    if (streamRef.current) return streamRef.current;
    if (!navigator.mediaDevices?.getUserMedia) {
      setState((s) => ({ ...s, status: "unsupported", error: `${Device} access requires HTTPS or localhost.` }));
      return null;
    }
    setState((s) => ({ ...s, status: "requesting", error: null }));
    try {
      const stream = await navigator.mediaDevices.getUserMedia(
        kind === "camera" ? { video: constraintsRef.current, audio: false } : { audio: constraintsRef.current, video: false },
      );
      const track = stream.getTracks()[0];
      streamRef.current = stream;
      track.addEventListener("ended", () => handleLost(`The ${kind} stopped delivering ${kind === "camera" ? "video" : "audio"} (device removed or access revoked).`));
      track.addEventListener("mute", () => setState((s) => ({ ...s, muted: true })));
      track.addEventListener("unmute", () => setState((s) => ({ ...s, muted: false })));
      setState((s) => ({
        ...s,
        status: "active",
        error: null,
        muted: track.muted,
        label: track.label || `Default ${kind}`,
        settings: track.getSettings(),
        stream,
      }));
      return stream;
    } catch (err) {
      const { status, message } = describeMediaError(err, kind);
      setState((s) => ({ ...s, status, error: message, stream: null }));
      return null;
    }
  }, [Device, handleLost, kind]);

  requestRef.current = request;

  const release = useCallback(() => {
    const stream = streamRef.current;
    streamRef.current = null;
    stream?.getTracks().forEach((t) => t.stop());
    setState((s) => ({ ...s, status: s.status === "active" ? "stopped" : s.status, muted: false, stream: null }));
  }, []);

  // Stop the hardware when the page unmounts or is closed.
  useEffect(() => {
    const stopAll = () => streamRef.current?.getTracks().forEach((t) => t.stop());
    window.addEventListener("pagehide", stopAll);
    return () => {
      window.removeEventListener("pagehide", stopAll);
      if (reconnectRef.current) clearTimeout(reconnectRef.current);
      stopAll();
    };
  }, []);

  /** Current live track (null if not acquired). */
  const getTrack = useCallback(() => streamRef.current?.getTracks()[0] ?? null, []);

  return { ...state, request, release, getTrack };
}
