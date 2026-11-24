/**
 * Writes already-encoded VP8 + Opus packets into a WebM file (Mediabunny).
 * Timestamps are rebased so the clip's first video keyframe is t=0; audio uses
 * the same base, so A/V alignment from capture is preserved exactly.
 */
import {
  BufferTarget,
  EncodedAudioPacketSource,
  EncodedPacket,
  EncodedVideoPacketSource,
  Output,
  WebMOutputFormat,
} from "mediabunny";
import type { TimedPacket } from "./mediaRing";

export interface MuxInput {
  video: TimedPacket[];
  audio: TimedPacket[];
  baseUs: number;
  frameRate: number;
  videoMeta: EncodedVideoChunkMetadata;
  audioMeta: EncodedAudioChunkMetadata;
}

export async function muxWebm(input: MuxInput): Promise<ArrayBuffer> {
  const output = new Output({ format: new WebMOutputFormat(), target: new BufferTarget() });
  const videoSource = new EncodedVideoPacketSource("vp8");
  const audioSource = new EncodedAudioPacketSource("opus");
  output.addVideoTrack(videoSource, { frameRate: input.frameRate });
  output.addAudioTrack(audioSource);
  await output.start();

  // Interleave by timestamp so the muxer can write clusters in order.
  const items = [
    ...input.video.map((p) => ({ p, kind: "video" as const })),
    ...input.audio.map((p) => ({ p, kind: "audio" as const })),
  ].sort((a, b) => a.p.ts - b.p.ts);

  let firstVideo = true;
  let firstAudio = true;
  for (const { p, kind } of items) {
    const packet = new EncodedPacket(p.data, p.key ? "key" : "delta", (p.ts - input.baseUs) / 1e6, p.dur / 1e6);
    if (kind === "video") {
      await videoSource.add(packet, firstVideo ? input.videoMeta : undefined);
      firstVideo = false;
    } else {
      await audioSource.add(packet, firstAudio ? input.audioMeta : undefined);
      firstAudio = false;
    }
  }
  await output.finalize();
  const buffer = (output.target as BufferTarget).buffer;
  if (!buffer) throw new Error("WebM muxer produced no data");
  return buffer;
}
