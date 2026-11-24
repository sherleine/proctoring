using System.Buffers.Binary;
using Proctoring.Application.Errors;

namespace Proctoring.Application.Events;

/// <summary>What the WAV header says about the audio.</summary>
public sealed record WavInfo(int FormatTag, int Channels, int SampleRate, int BitsPerSample, int BlockAlign, int ByteRate, long DataBytes)
{
    public double DurationSeconds => ByteRate > 0 ? (double)DataBytes / ByteRate : 0;
}

/// <summary>
/// Reads and checks a WAV file's actual header (RIFF chunks), independent of file name or Content-Type.
///
/// A WAV file is a RIFF container: "RIFF" + size + "WAVE", then chunks of (4-byte id, 4-byte size, data).
/// The "fmt " chunk describes the samples (encoding, channels, rate, bits); the "data" chunk holds them.
/// </summary>
public static class WavValidator
{
    public const int PcmFormatTag = 1;
    private const int MaxChunks = 64;

    /// <summary>Parses the header. Throws <see cref="InvalidAudioException"/> for anything that is not a well-formed WAV.</summary>
    public static async Task<WavInfo> ReadAsync(Stream stream, long fileLength, CancellationToken ct)
    {
        if (fileLength == 0) throw new InvalidAudioException(InvalidAudioException.Empty, "The audio file is empty");
        if (fileLength < 44) throw new InvalidAudioException(InvalidAudioException.Corrupted, "The file is too short to be a WAV file");

        var header = new byte[12];
        await ReadExactlyAsync(stream, header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidAudioException(InvalidAudioException.Corrupted, "Not a WAV file (missing RIFF/WAVE header)");
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (riffSize + 8L != fileLength)
            throw new InvalidAudioException(InvalidAudioException.Corrupted,
                $"Corrupted WAV: the RIFF header declares {riffSize + 8L} bytes but the file has {fileLength}");

        long position = 12;
        byte[]? fmt = null;
        long? dataBytes = null;
        var chunkHeader = new byte[8];
        for (var i = 0; i < MaxChunks && position + 8 <= fileLength; i++)
        {
            await ReadExactlyAsync(stream, chunkHeader, ct);
            position += 8;
            var id = System.Text.Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            if (position + size > fileLength)
                throw new InvalidAudioException(InvalidAudioException.Corrupted, $"Corrupted WAV: chunk '{id.Trim()}' extends past the end of the file");

            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: 'fmt ' chunk is too short");
                fmt = new byte[size];
                await ReadExactlyAsync(stream, fmt, ct);
            }
            else if (id == "data")
            {
                if (fmt is null) throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: 'data' chunk before 'fmt ' chunk");
                dataBytes = size;
                break; // samples follow; no need to read them for header validation
            }
            else
            {
                await SkipAsync(stream, size, ct); // e.g. LIST metadata chunks
            }
            position += size + (size % 2); // chunks are padded to an even length
            if (size % 2 == 1 && position <= fileLength) await SkipAsync(stream, 1, ct);
        }

        if (fmt is null) throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: no 'fmt ' chunk");
        if (dataBytes is null) throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: no 'data' chunk");

        var info = new WavInfo(
            FormatTag: BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0)),
            Channels: BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2)),
            SampleRate: (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4)),
            BitsPerSample: BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14)),
            BlockAlign: BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12)),
            ByteRate: (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(8)),
            DataBytes: dataBytes.Value);

        if (info.Channels == 0 || info.SampleRate == 0 || info.BitsPerSample == 0)
            throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: zero channels, sample rate or bit depth");
        if (info.BlockAlign != info.Channels * info.BitsPerSample / 8 || info.ByteRate != info.SampleRate * info.BlockAlign)
            throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: inconsistent block align / byte rate");
        if (info.DataBytes == 0) throw new InvalidAudioException(InvalidAudioException.Empty, "The audio contains no samples");
        if (info.DataBytes % info.BlockAlign != 0)
            throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: data length is not a whole number of samples");
        return info;
    }

    /// <summary>Checks the stream format this API accepts: 16-bit PCM, mono, 16 kHz.</summary>
    public static void RequireFormat(WavInfo info, int sampleRate, int channels, int bitsPerSample)
    {
        if (info.FormatTag != PcmFormatTag)
            throw new InvalidAudioException(InvalidAudioException.Unsupported,
                $"Unsupported WAV encoding (format tag {info.FormatTag}); only uncompressed PCM (format tag 1) is accepted");
        if (info.BitsPerSample != bitsPerSample)
            throw new InvalidAudioException(InvalidAudioException.Unsupported, $"Unsupported bit depth {info.BitsPerSample}; {bitsPerSample}-bit is required");
        if (info.Channels != channels)
            throw new InvalidAudioException(InvalidAudioException.Unsupported, $"Unsupported channel count {info.Channels}; mono ({channels}) is required");
        if (info.SampleRate != sampleRate)
            throw new InvalidAudioException(InvalidAudioException.Unsupported, $"Unsupported sample rate {info.SampleRate} Hz; {sampleRate} Hz is required");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: unexpected end of file");
        }
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        var buffer = new byte[4096];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (read == 0) throw new InvalidAudioException(InvalidAudioException.Corrupted, "Corrupted WAV: unexpected end of file");
            count -= read;
        }
    }
}
