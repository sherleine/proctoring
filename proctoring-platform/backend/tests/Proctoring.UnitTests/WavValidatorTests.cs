using System.Buffers.Binary;
using Proctoring.Application.Errors;
using Proctoring.Application.Events;
using Proctoring.Tests.Shared;

namespace Proctoring.UnitTests;

public class WavValidatorTests
{
    private static async Task<WavInfo> Read(byte[] bytes) =>
        await WavValidator.ReadAsync(new MemoryStream(bytes), bytes.Length, default);

    private static async Task<InvalidAudioException> Rejected(byte[] bytes, bool checkFormat = true)
    {
        return await Assert.ThrowsAsync<InvalidAudioException>(async () =>
        {
            var info = await Read(bytes);
            if (checkFormat) WavValidator.RequireFormat(info, 16000, 1, 16);
        });
    }

    [Fact]
    public async Task Accepts_16kHz_mono_16bit_pcm_and_reports_duration()
    {
        var info = await Read(TestAudio.Wav(seconds: 2.5));
        WavValidator.RequireFormat(info, 16000, 1, 16);
        Assert.Equal(1, info.FormatTag);
        Assert.Equal(80_000, info.DataBytes);
        Assert.Equal(2.5, info.DurationSeconds, 6);
    }

    [Fact]
    public async Task Skips_extra_chunks_such_as_LIST()
    {
        var info = await Read(TestAudio.Wav(seconds: 1, listChunk: true));
        Assert.Equal(1.0, info.DurationSeconds, 6);
    }

    [Theory]
    [InlineData(48000, 1, 16, 1, "sample rate 48000")]
    [InlineData(44100, 1, 16, 1, "sample rate 44100")]
    [InlineData(16000, 2, 16, 1, "channel count 2")]
    [InlineData(16000, 1, 8, 1, "bit depth 8")]
    [InlineData(16000, 1, 32, 3, "format tag 3")] // IEEE float
    [InlineData(16000, 1, 16, 0xFFFE, "format tag 65534")] // WAVE_FORMAT_EXTENSIBLE
    [InlineData(16000, 1, 16, 85, "format tag 85")] // MP3 in a WAV container
    public async Task Rejects_unsupported_formats(int rate, int channels, int bits, int tag, string expected)
    {
        var ex = await Rejected(TestAudio.Wav(seconds: 0.5, sampleRate: rate, channels: channels, bitsPerSample: bits, formatTag: tag));
        Assert.Equal(InvalidAudioException.Unsupported, ex.Kind);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Rejects_an_empty_file()
    {
        var ex = await Rejected([]);
        Assert.Equal(InvalidAudioException.Empty, ex.Kind);
    }

    [Fact]
    public async Task Rejects_a_wav_without_samples()
    {
        var ex = await Rejected(TestAudio.Wav(seconds: 0));
        Assert.Equal(InvalidAudioException.Empty, ex.Kind);
    }

    public static TheoryData<string, Func<byte[], byte[]>> CorruptedCases() => new()
    {
        { "not RIFF", b => { b[0] = (byte)'X'; return b; } },
        { "not WAVE", b => { b[8] = (byte)'X'; return b; } },
        { "truncated", b => b[..60] },
        { "RIFF size too large", b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)b.Length + 100); return b; } },
        { "trailing garbage", b => TestAudio.Concat(b, new byte[10]) },
        { "data chunk past end", b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)b.Length * 2); return b; } },
        { "byte rate inconsistent", b => { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 12345); return b; } },
        { "odd sample data", b => { var d = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), d - 1); return b; } },
        { "no fmt chunk", b => { b[12] = (byte)'x'; return b; } },
        { "random bytes", _ => Enumerable.Range(0, 500).Select(i => (byte)(i * 37)).ToArray() },
    };

    [Theory]
    [MemberData(nameof(CorruptedCases))]
    public async Task Rejects_corrupted_files(string name, Func<byte[], byte[]> corrupt)
    {
        var ex = await Rejected(corrupt(TestAudio.Wav(seconds: 0.5)), checkFormat: false);
        Assert.True(ex.Kind is InvalidAudioException.Corrupted or InvalidAudioException.Empty, $"{name}: {ex.Kind} {ex.Message}");
    }

    [Fact]
    public async Task A_renamed_non_wav_file_is_rejected_by_content()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[200]).ToArray();
        var ex = await Rejected(png, checkFormat: false);
        Assert.Contains("RIFF", ex.Message);
    }
}
