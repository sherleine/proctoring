/**
 * Main-thread handle for event-based recording. Clones the live camera and
 * microphone tracks (the preview keeps the originals), hands their frames to the
 * event worker via MediaStreamTrackProcessor, and relays worker messages.
 */
import type { EventWorkerConfig, EventWorkerInbound, EventWorkerOutbound } from "./eventMessages";

export function eventRecordingSupport(): string | null {
  const missing = ["MediaStreamTrackProcessor", "VideoEncoder", "AudioEncoder"].filter((name) => !(name in globalThis));
  return missing.length ? `This browser cannot record events (missing ${missing.join(", ")}). Use Chrome or Edge.` : null;
}

export class EventMonitor {
  private worker: Worker | null = null;
  private clones: MediaStreamTrack[] = [];
  private stopped: (() => void) | null = null;

  constructor(
    private readonly config: EventWorkerConfig,
    private readonly onMessage: (msg: EventWorkerOutbound) => void,
  ) {}

  /** Resolves once both encoders are running and the VAD model is loaded. */
  async start(videoTrack: MediaStreamTrack, audioTrack: MediaStreamTrack): Promise<void> {
    const unsupported = eventRecordingSupport();
    if (unsupported) throw new Error(unsupported);
    const video = videoTrack.clone();
    const audio = audioTrack.clone();
    this.clones = [video, audio];
    const videoProcessor = new MediaStreamTrackProcessor({ track: video as MediaStreamVideoTrack, maxBufferSize: 2 });
    const audioProcessor = new MediaStreamTrackProcessor({ track: audio as MediaStreamAudioTrack, maxBufferSize: 100 });

    const worker = new Worker(new URL("./eventRecorder.worker.ts", import.meta.url), { type: "module" });
    this.worker = worker;
    try {
      await new Promise<void>((resolve, reject) => {
        worker.onerror = (e) => reject(new Error(e.message || "Event recorder failed to start"));
        worker.onmessage = (e: MessageEvent<EventWorkerOutbound>) => {
          const msg = e.data;
          if (msg.type === "ready") resolve();
          else if (msg.type === "error" && msg.fatal) reject(new Error(msg.message));
          if (msg.type === "stopped") this.stopped?.();
          this.onMessage(msg);
        };
        const init: EventWorkerInbound = {
          type: "init",
          config: this.config,
          video: videoProcessor.readable,
          audio: audioProcessor.readable,
        };
        worker.postMessage(init, [videoProcessor.readable, audioProcessor.readable] as unknown as Transferable[]);
      });
    } catch (err) {
      this.dispose();
      throw err;
    }
  }

  /** Finishes any event in progress (it is saved), then releases everything. */
  async stop(timeoutMs = 8000): Promise<void> {
    const worker = this.worker;
    if (worker) {
      await Promise.race([
        new Promise<void>((resolve) => {
          this.stopped = resolve;
          worker.postMessage({ type: "stop" } satisfies EventWorkerInbound);
        }),
        new Promise<void>((resolve) => setTimeout(resolve, timeoutMs)),
      ]);
    }
    this.dispose();
  }

  private dispose(): void {
    this.worker?.terminate();
    this.worker = null;
    this.clones.forEach((t) => t.stop());
    this.clones = [];
  }
}
