using System.Text.Json;
using System.Text.Json.Nodes;
using Proctoring.Application.Errors;
using Proctoring.Application.Events;
using Proctoring.Infrastructure.Queue;
using Proctoring.Infrastructure.Storage;
using Proctoring.Tests.Shared;

namespace Proctoring.UnitTests;

public sealed class LocalFileAudioStoreTests : IDisposable
{
    private const string Session = "ses_01JABCDEFGHJKMNPQRSTVWXYZ";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"proctoring-store-{Guid.NewGuid():N}");
    private readonly LocalFileAudioStore _store;

    public LocalFileAudioStoreTests() => _store = new LocalFileAudioStore(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string[] StagingFiles() => Directory.GetFiles(Path.Combine(_root, ".staging"));

    [Fact]
    public async Task Stage_records_size_and_sha256_then_commit_moves_the_file_atomically()
    {
        var wav = TestAudio.Wav(1);
        var staged = await _store.StageAsync(new MemoryStream(wav), 1_000_000, default);
        Assert.Equal(wav.Length, staged.SizeBytes);
        Assert.Equal(TestAudio.Sha256(wav), staged.Sha256);

        var key = _store.NewStorageKey(Session);
        await _store.CommitAsync(staged, key, default);
        await staged.DisposeAsync(); // committed: must not delete

        Assert.True(await _store.ExistsAsync(key, default));
        Assert.Equal(wav, await File.ReadAllBytesAsync(Path.Combine(_root, key)));
        Assert.Empty(StagingFiles());
        Assert.Matches("^ses_[A-Za-z0-9]+/[a-f0-9]{32}\\.wav$", key);
    }

    [Fact]
    public async Task Too_large_uploads_stop_early_and_leave_no_file()
    {
        await Assert.ThrowsAsync<AudioTooLargeException>(() => _store.StageAsync(new MemoryStream(new byte[50_000]), 10_000, default));
        Assert.Empty(StagingFiles());
    }

    [Fact]
    public async Task Interrupted_uploads_leave_no_file()
    {
        await Assert.ThrowsAsync<IOException>(() => _store.StageAsync(new FailingStream(20_000), 1_000_000, default));
        Assert.Empty(StagingFiles());
    }

    [Fact]
    public async Task Disposing_uncommitted_audio_deletes_it()
    {
        var staged = await _store.StageAsync(new MemoryStream(TestAudio.Wav(0.5)), 1_000_000, default);
        Assert.Single(StagingFiles());
        await staged.DisposeAsync();
        Assert.Empty(StagingFiles());
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("ses_01JABCDEFGHJKMNPQRSTVWXYZ/../../evil.wav")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("ses_01JABCDEFGHJKMNPQRSTVWXYZ/notes.txt")]
    [InlineData("")]
    public async Task Keys_that_are_not_server_generated_are_refused(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ExistsAsync(key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(key, default));
    }

    [Theory]
    [InlineData("../ses_x")]
    [InlineData("ses_01JABCDEFGHJKMNPQRSTVWXYZ/..")]
    public void Session_ids_cannot_inject_paths(string sessionId) =>
        Assert.Throws<ArgumentException>(() => _store.NewStorageKey(sessionId));

    [Fact]
    public async Task Stale_staging_files_are_cleaned_up()
    {
        var old = await _store.StageAsync(new MemoryStream(TestAudio.Wav(0.2)), 1_000_000, default);
        var fresh = await _store.StageAsync(new MemoryStream(TestAudio.Wav(0.2)), 1_000_000, default);
        var oldFile = StagingFiles().First();
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddHours(-3));
        Assert.Equal(1, await _store.DeleteStaleStagingAsync(TimeSpan.FromHours(1), default));
        Assert.Single(StagingFiles());
        await old.DisposeAsync();
        await fresh.DisposeAsync();
    }

    /// <summary>Delivers some bytes, then fails like a dropped connection.</summary>
    internal sealed class FailingStream(int bytesBeforeFailure) : Stream
    {
        private int _sent;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent >= bytesBeforeFailure) throw new IOException("connection reset");
            var n = Math.Min(buffer.Length, bytesBeforeFailure - _sent);
            buffer.Span[..n].Fill(1);
            _sent += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class LocalDirectoryQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"proctoring-queue-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static AudioEventSubmittedMessage Message(Guid eventId, string payload = "{}") =>
        new(Guid.NewGuid().ToString(), eventId.ToString(), "ses_01JABCDEFGHJKMNPQRSTVWXYZ", eventId.ToString(),
            new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc), payload);

    [Fact]
    public async Task Writes_the_stream_entry_fields_and_is_idempotent_per_event()
    {
        var queue = new LocalDirectoryAudioEventQueue(_dir);
        var id = Guid.NewGuid();
        await queue.PublishAsync(Message(id, "{\"a\":1}"), default);
        await queue.PublishAsync(Message(id, "{\"a\":2}"), default); // re-publish overwrites, no second entry

        var file = Assert.Single(Directory.GetFiles(_dir));
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(file))!;
        Assert.Equal("audio.event.submitted", fields["messageType"]);
        Assert.Equal("1", fields["schemaVersion"]);
        Assert.Equal(id.ToString(), fields["correlationId"]);
        Assert.Equal("{\"a\":2}", fields["payload"]);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task An_unwritable_queue_fails_loudly_instead_of_dropping_the_event()
    {
        var queue = new LocalDirectoryAudioEventQueue(_dir);
        var id = Guid.NewGuid();
        // Occupy the target file name with a directory so the write cannot succeed.
        Directory.CreateDirectory(Path.Combine(_dir, $"ses_01JABCDEFGHJKMNPQRSTVWXYZ_{id:N}.json"));
        await Assert.ThrowsAsync<QueueUnavailableException>(() => queue.PublishAsync(Message(id), default));
    }
}
