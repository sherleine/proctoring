
import workletUrl from "./capture.worklet.ts?worker&url";
import type { DetectorConfig } from "./eventDetector";
import type { WorkerIn, WorkerOut } from "./vad.worker";

export type PipelineEvent =
  | Exclude<WorkerOut, { type: "ready" } | { type: "flushed" } | { type: "started" }>
  | { type: "context-state"; state: AudioContextState | "interrupted" };

export interface PipelineOptions {
  modelUrl: string;
  detector: DetectorConfig;
  onMessage(message: PipelineEvent): void;
}

export class AudioPipeline {
  private ctx: AudioContext | null = null;
  private worker: Worker | null = null;
  private node: AudioWorkletNode | null = null;
  private source: MediaStreamAudioSourceNode | null = null;
  private sample0EpochMs: number | null = null;
  private flushWaiter: (() => void) | null = null;

  constructor(private readonly options: PipelineOptions) {}

  
  epochOfSample(sample: number): number {
    if (this.sample0EpochMs === null) throw new Error("Audio clock not started yet");
    return this.sample0EpochMs + (sample / 16_000) * 1000;
  }

  get inputSampleRate(): number | null {
    return this.ctx?.sampleRate ?? null;
  }

  get contextState(): string {
    return this.ctx?.state ?? "closed";
  }

  
  async resume(): Promise<void> {
    if (this.ctx && this.ctx.state !== "running" && this.ctx.state !== "closed") await this.ctx.resume().catch(() => undefined);
  }

  
  async start(stream: MediaStream): Promise<void> {
    if (typeof AudioWorkletNode === "undefined" || typeof WebAssembly === "undefined")
      throw new Error("This browser does not support AudioWorklet/WebAssembly. Use a current Chrome or Edge.");

    const worker = new Worker(new URL("./vad.worker.ts", import.meta.url), { type: "module" });
    this.worker = worker;
    const ready = new Promise<void>((resolve, reject) => {
      worker.onmessage = (e: MessageEvent<WorkerOut>) => {
        const msg = e.data;
        if (msg.type === "ready") resolve();
        else if (msg.type === "error" && msg.stage === "init") reject(new Error(`Speech detection could not start: ${msg.message}`));
        else this.handle(msg);
      };
      worker.onerror = (e) => reject(new Error(`Speech detection worker failed: ${e.message}`));
    });
    worker.postMessage({ type: "init", modelUrl: this.options.modelUrl, ortBaseUrl: null, detector: this.options.detector } satisfies WorkerIn);
    await ready;

    const ctx = new AudioContext({ latencyHint: "interactive" });
    this.ctx = ctx;
    
    ctx.onstatechange = () => {
      this.options.onMessage({ type: "context-state", state: ctx.state as AudioContextState });
      if (ctx.state === "suspended" || (ctx.state as string) === "interrupted") void ctx.resume().catch(() => undefined);
    };
    try {
      await ctx.audioWorklet.addModule(workletUrl);
    } catch (err) {
      throw new Error(`Audio processing (AudioWorklet) could not start: ${(err as Error).message}`);
    }
    const node = new AudioWorkletNode(ctx, "capture-processor", { numberOfInputs: 1, numberOfOutputs: 1, channelCount: 1, channelCountMode: "explicit" });
    this.node = node;
    const channel = new MessageChannel();
    node.port.postMessage({ type: "port", port: channel.port1 }, [channel.port1]);
    worker.postMessage({ type: "port", port: channel.port2 } satisfies WorkerIn, [channel.port2]);

    this.source = ctx.createMediaStreamSource(stream);
    const mute = ctx.createGain();
    mute.gain.value = 0; // keeps the graph pulling audio without playing the microphone back
    this.source.connect(node).connect(mute).connect(ctx.destination);
    if (ctx.state === "suspended") await ctx.resume();
  }

  
  async flush(timeoutMs = 3000): Promise<void> {
    if (!this.worker) return;
    const done = new Promise<void>((resolve) => (this.flushWaiter = resolve));
    this.worker.postMessage({ type: "flush" } satisfies WorkerIn);
    await Promise.race([done, new Promise((r) => setTimeout(r, timeoutMs))]);
  }

  async stop(): Promise<void> {
    this.node?.port.postMessage({ type: "stop" });
    this.source?.disconnect();
    this.node?.disconnect();
    if (this.ctx && this.ctx.state !== "closed") await this.ctx.close();
    this.worker?.terminate();
    this.ctx = null;
    this.worker = null;
    this.node = null;
    this.source = null;
  }

  private handle(msg: WorkerOut): void {
    if (msg.type === "started") {
      
      
      const ctx = this.ctx!;
      const ts = ctx.getOutputTimestamp();
      const valid = (ts.performanceTime ?? 0) > 0 && (ts.contextTime ?? 0) > 0;
      const nowEpoch = valid ? performance.timeOrigin + ts.performanceTime! : Date.now();
      const nowContext = valid ? ts.contextTime! : ctx.currentTime;
      this.sample0EpochMs = nowEpoch - (nowContext - msg.contextTime) * 1000;
      return;
    }
    if (msg.type === "flushed") {
      this.flushWaiter?.();
      this.flushWaiter = null;
      return;
    }
    if (msg.type === "ready") return;
    this.options.onMessage(msg);
  }
}
