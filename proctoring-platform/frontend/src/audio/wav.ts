

export const WAV_SAMPLE_RATE = 16_000;
const HEADER_BYTES = 44;

 
export function encodeWav(samples: Float32Array, sampleRate = WAV_SAMPLE_RATE): ArrayBuffer {
  const dataBytes = samples.length * 2;
  const buffer = new ArrayBuffer(HEADER_BYTES + dataBytes);
  const view = new DataView(buffer);
  const ascii = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
  };
  ascii(0, "RIFF");
  view.setUint32(4, 36 + dataBytes, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  view.setUint32(16, 16, true); 
  view.setUint16(20, 1, true); 
  view.setUint16(22, 1, true); 
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true); 
  view.setUint16(32, 2, true); 
  view.setUint16(34, 16, true); 
  ascii(36, "data");
  view.setUint32(40, dataBytes, true);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(HEADER_BYTES + i * 2, Math.round(s < 0 ? s * 0x8000 : s * 0x7fff), true);
  }
  return buffer;
}

export interface WavInfo {
  sampleRate: number;
  channels: number;
  bitsPerSample: number;
  dataBytes: number;
  durationSeconds: number;
}


export function validateWav(buffer: ArrayBuffer): WavInfo {
  const view = new DataView(buffer);
  const tag = (offset: number) => String.fromCharCode(...new Uint8Array(buffer, offset, 4));
  if (buffer.byteLength < HEADER_BYTES || tag(0) !== "RIFF" || tag(8) !== "WAVE") throw new Error("Not a RIFF/WAVE file");
  let offset = 12;
  let fmt: { format: number; channels: number; sampleRate: number; bits: number } | null = null;
  let dataBytes = -1;
  while (offset + 8 <= buffer.byteLength) {
    const id = tag(offset);
    const size = view.getUint32(offset + 4, true);
    if (id === "fmt ") {
      fmt = { format: view.getUint16(offset + 8, true), channels: view.getUint16(offset + 10, true), sampleRate: view.getUint32(offset + 12, true), bits: view.getUint16(offset + 22, true) };
    } else if (id === "data") {
      dataBytes = size;
      break;
    }
    offset += 8 + size + (size % 2);
  }
  if (!fmt) throw new Error("WAV has no fmt chunk");
  if (dataBytes < 0) throw new Error("WAV has no data chunk");
  if (fmt.format !== 1) throw new Error("WAV is not PCM");
  if (fmt.channels !== 1) throw new Error(`WAV must be mono (has ${fmt.channels} channels)`);
  if (fmt.sampleRate !== WAV_SAMPLE_RATE) throw new Error(`WAV must be 16000 Hz (is ${fmt.sampleRate} Hz)`);
  if (fmt.bits !== 16) throw new Error(`WAV must be 16-bit (is ${fmt.bits}-bit)`);
  if (offset + 8 + dataBytes > buffer.byteLength) throw new Error("WAV data chunk is truncated");
  if (dataBytes === 0) throw new Error("WAV contains no samples");
  return { sampleRate: fmt.sampleRate, channels: 1, bitsPerSample: 16, dataBytes, durationSeconds: dataBytes / 2 / fmt.sampleRate };
}
