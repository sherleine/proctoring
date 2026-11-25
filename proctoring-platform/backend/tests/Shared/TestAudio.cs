using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Proctoring.Tests.Shared;

/// <summary>Builds real WAV files (and deliberately broken ones) plus contract-valid event metadata.</summary>
public static class TestAudio
{
    public static byte[] Wav(double seconds = 2.0, int sampleRate = 16000, int channels = 1, int bitsPerSample = 16,
        int formatTag = 1, double frequency = 440, bool listChunk = false)
    {
        var blockAlign = channels * bitsPerSample / 8;
        var frames = (int)Math.Round(seconds * sampleRate);
        var data = new byte[frames * blockAlign];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 8000);
            for (var c = 0; c < channels; c++)
            {
                var offset = i * blockAlign + c * (bitsPerSample / 8);
                if (bitsPerSample == 16) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset), sample);
                else if (bitsPerSample == 8) data[offset] = (byte)(128 + sample / 256);
                else if (bitsPerSample == 32) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), sample / 32768f);
            }
        }

        var list = listChunk ? Chunk("LIST", Encoding.ASCII.GetBytes("INFOISFT\u0005\0\0\0test\0\0")) : [];
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bitsPerSample);

        var body = Concat(Encoding.ASCII.GetBytes("WAVE"), Chunk("fmt ", fmt), list, Chunk("data", data));
        return Concat(Encoding.ASCII.GetBytes("RIFF"), U32(body.Length), body);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Metadata valid against audio-event-metadata.v1.json for <paramref name="wav"/>.</summary>
    public static JsonObject Metadata(byte[] wav, Guid eventId, DateTime startedAtUtc, double? durationSeconds = null, int sequence = 0,
        string eventType = "speech")
    {
        var duration = durationSeconds ?? (wav.Length - 44) / 32000.0;
        return new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["eventId"] = eventId.ToString(),
            ["eventType"] = eventType,
            ["sequenceNumber"] = sequence,
            ["part"] = new JsonObject { ["groupId"] = Guid.NewGuid().ToString(), ["index"] = 0, ["isFinal"] = true, ["overlapSeconds"] = 0 },
            ["startedAt"] = Iso(startedAtUtc),
            ["endedAt"] = Iso(startedAtUtc.AddSeconds(duration)),
            ["speechStartedAt"] = Iso(startedAtUtc.AddSeconds(Math.Min(0.5, duration / 2))),
            ["durationSeconds"] = duration,
            ["preEventSeconds"] = Math.Min(0.5, duration / 2),
            ["trigger"] = new JsonObject
            {
                ["type"] = eventType == "enrolment" ? "enrolment" : "vad", ["model"] = "silero-vad-v5", ["startThreshold"] = 0.5, ["endThreshold"] = 0.35,
                ["maxProbability"] = 0.97, ["speechSeconds"] = duration / 2, ["noiseFloorDbfs"] = -60.0,
            },
            ["audio"] = new JsonObject
            {
                ["format"] = "wav", ["codec"] = "pcm_s16le", ["sampleRate"] = 16000, ["channels"] = 1,
                ["sizeBytes"] = wav.Length, ["sha256"] = Sha256(wav),
            },
            ["video"] = new JsonObject { ["recordedLocally"] = true, ["uploaded"] = false },
        };
    }

    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static byte[] Chunk(string id, byte[] payload)
    {
        var padded = payload.Length % 2 == 1 ? Concat(payload, [0]) : payload;
        return Concat(Encoding.ASCII.GetBytes(id), U32(payload.Length), padded);
    }

    private static byte[] U32(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value);
        return b;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }
}
