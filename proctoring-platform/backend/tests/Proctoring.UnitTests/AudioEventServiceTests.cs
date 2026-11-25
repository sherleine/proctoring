using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Proctoring.Application.Errors;
using Proctoring.Application.Events;
using Proctoring.Application.Sessions;
using Proctoring.Contracts.Sessions;
using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;
using Proctoring.Infrastructure.Persistence;
using Proctoring.Infrastructure.Security;
using Proctoring.Infrastructure.Storage;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;

namespace Proctoring.UnitTests;

/// <summary>Queue double that records messages and can be switched to fail.</summary>
public sealed class RecordingQueue : IAudioEventQueue
{
    public ConcurrentQueue<AudioEventSubmittedMessage> Messages { get; } = new();
    public volatile bool Fail;
    public string Name => "test";

    public Task PublishAsync(AudioEventSubmittedMessage message, CancellationToken ct)
    {
        if (Fail) throw new QueueUnavailableException("test queue is down");
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Everything an upload touches, wired for tests: SQLite file (migrated), real local file store, recording queue,
/// fake clock. Each operation uses a fresh DbContext, like one HTTP request.
/// </summary>
public sealed class EventHarness : IDisposable
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"proctoring-ev-{Guid.NewGuid():N}.db");
    public string StoreRoot { get; } = Path.Combine(Path.GetTempPath(), $"proctoring-ev-store-{Guid.NewGuid():N}");
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
    public RecordingQueue Queue { get; } = new();
    public LocalFileAudioStore Store { get; }
    public AudioEventOptions EventOptions { get; } = new();
    public SessionOptions SessionOptions { get; } = new() { MaxDurationHours = 6, UploadGraceSeconds = 120 };
    public RecordingPolicyOptions Policy { get; } = new() { MaxEventSeconds = 10, MaxUploadBytes = 2_000_000 };
    public Func<IAudioStore, IAudioStore> WrapStore { get; set; } = s => s;
    public Func<IAudioEventRepository, IAudioEventRepository> WrapRepository { get; set; } = r => r;

    private static readonly JsonSchemaContractValidator Contracts = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private readonly SessionTokenSecret _secret = SessionTokenServiceTests.NewSecret();

    public EventHarness()
    {
        Store = new LocalFileAudioStore(StoreRoot);
        using var db = NewDb();
        db.Database.Migrate();
    }

    public ProctoringDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProctoringDbContext>().UseSqlite($"Data Source={DbPath};Pooling=False;Default Timeout=30").Options);

    public sealed record Scope(ProctoringDbContext Db, SessionService Sessions, AudioEventService Events, EventMaintenance Maintenance, IAudioStore Store) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    public Scope NewScope()
    {
        var db = NewDb();
        var sessions = new SessionService(new EfSessionRepository(db), new SessionTokenService(_secret), new SessionIdGenerator(Clock),
            Clock, Options.Create(SessionOptions), NullLogger<SessionService>.Instance);
        var repo = WrapRepository(new EfAudioEventRepository(db, new Proctoring.Infrastructure.Notifications.InProcessEventUpdateNotifier()));
        var store = WrapStore(Store);
        var publisher = new EventPublisher(repo, store, Queue, Contracts, Clock, Options.Create(EventOptions), NullLogger<EventPublisher>.Instance,
            new EfVoiceprintRepository(db));
        var events = new AudioEventService(sessions, repo, store, publisher, Contracts, Clock, Options.Create(EventOptions),
            Options.Create(SessionOptions), Options.Create(Policy), NullLogger<AudioEventService>.Instance);
        var maintenance = new EventMaintenance(repo, store, publisher, Clock, Options.Create(EventOptions), NullLogger<EventMaintenance>.Instance,
            new EfVoiceprintRepository(db));
        return new Scope(db, sessions, events, maintenance, store);
    }

    public async Task<(Session Session, string Token)> NewSessionAsync()
    {
        await using var s = NewScope();
        var created = await s.Sessions.CreateAsync(new CreateSessionRequest("cand-777", "exam-9", null), null, default);
        return (created.Session, created.Token);
    }

    /// <summary>Authorizes and uploads like the endpoint does (stage -> upload -> dispose).</summary>
    public async Task<UploadResult> UploadAsync(string sessionId, string token, JsonNode metadata, byte[] wav)
    {
        await using var s = NewScope();
        var session = await s.Events.AuthorizeUploadAsync(sessionId, token, default);
        await using var staged = await s.Store.StageAsync(new MemoryStream(wav), Policy.MaxUploadBytes, default);
        return await s.Events.UploadAsync(session, metadata, staged, default);
    }

    public string[] StoredFiles() => Directory.GetFiles(StoreRoot, "*.wav", SearchOption.AllDirectories);
    public string[] StagingFiles() => Directory.GetFiles(Path.Combine(StoreRoot, ".staging"));

    public async Task<AudioEvent> LoadAsync(string sessionId, Guid eventId)
    {
        await using var db = NewDb();
        return await db.AudioEvents.SingleAsync(e => e.SessionId == sessionId && e.EventId == eventId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(DbPath); } catch (IOException) { }
        try { Directory.Delete(StoreRoot, true); } catch (IOException) { }
    }
}

public sealed class AudioEventServiceTests : IDisposable
{
    private readonly EventHarness _h = new();
    public void Dispose() => _h.Dispose();

    private DateTime Now => _h.Clock.GetUtcNow().UtcDateTime;

    private static JsonObject Meta(byte[] wav, Guid id, DateTime start) => TestAudio.Metadata(wav, id, start);

    [Fact]
    public async Task Valid_upload_is_stored_and_queued_with_a_contract_valid_message()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(2);
        var id = Guid.NewGuid();

        var result = await _h.UploadAsync(session.Id, token, Meta(wav, id, Now), wav);

        Assert.Equal(UploadOutcome.Accepted, result.Outcome);
        Assert.Equal(AudioEventStatus.Queued, result.Event.Status);
        var stored = await _h.LoadAsync(session.Id, id);
        Assert.Equal("cand-777", stored.CandidateId); // from the session, not the upload
        Assert.Equal("exam-9", stored.ExamId);
        Assert.Equal(2.0, stored.DurationSeconds, 3);
        Assert.Equal(TestAudio.Sha256(wav), stored.AudioSha256);
        var file = Assert.Single(_h.StoredFiles());
        Assert.Equal(wav, await File.ReadAllBytesAsync(file));
        Assert.Empty(_h.StagingFiles());

        var message = Assert.Single(_h.Queue.Messages);
        Assert.Equal(id.ToString(), message.CorrelationId);
        var contracts = new JsonSchemaContractValidator(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
        Assert.Empty(contracts.Validate("audio-event-message.v1.json", JsonNode.Parse(message.PayloadJson)));
        Assert.Empty(contracts.Validate("stream-envelope.v1.json", JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(message.ToFields()))));
        Assert.Equal("cand-777", (string?)JsonNode.Parse(message.PayloadJson)!["candidateId"]);
    }

    [Fact]
    public async Task Identical_retry_returns_the_existing_event_without_a_second_row_file_or_message()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        var meta = Meta(wav, id, Now);
        await _h.UploadAsync(session.Id, token, meta, wav);

        // Same content, keys in a different order: still the same upload.
        var reordered = new JsonObject(meta.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        var retry = await _h.UploadAsync(session.Id, token, reordered, wav);

        Assert.Equal(UploadOutcome.Duplicate, retry.Outcome);
        Assert.Equal(AudioEventStatus.Queued, retry.Event.Status);
        Assert.Single(_h.StoredFiles());
        Assert.Single(_h.Queue.Messages);
        Assert.Empty(_h.StagingFiles());
        await using var db = _h.NewDb();
        Assert.Equal(1, await db.AudioEvents.CountAsync());
    }

    [Fact]
    public async Task Same_event_id_with_different_audio_or_metadata_is_a_conflict()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        await _h.UploadAsync(session.Id, token, Meta(wav, id, Now), wav);

        var otherAudio = TestAudio.Wav(1, frequency: 880);
        await Assert.ThrowsAsync<EventConflictException>(() => _h.UploadAsync(session.Id, token, Meta(otherAudio, id, Now), otherAudio));
        var otherMeta = Meta(wav, id, Now);
        otherMeta["sequenceNumber"] = 5;
        await Assert.ThrowsAsync<EventConflictException>(() => _h.UploadAsync(session.Id, token, otherMeta, wav));
        Assert.Single(_h.StoredFiles());
        Assert.Empty(_h.StagingFiles());
    }

    [Fact]
    public async Task Simultaneous_duplicate_uploads_create_one_event_one_file_and_one_message()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1.5);
        var id = Guid.NewGuid();
        var meta = Meta(wav, id, Now);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _h.UploadAsync(session.Id, token, meta.DeepClone(), wav))));

        Assert.Single(results, r => r.Outcome == UploadOutcome.Accepted);
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { UploadOutcome.Accepted, UploadOutcome.Duplicate }));
        Assert.Single(_h.StoredFiles());
        Assert.Single(_h.Queue.Messages);
        Assert.Empty(_h.StagingFiles());
        await using var db = _h.NewDb();
        Assert.Equal(1, await db.AudioEvents.CountAsync());
    }

    [Fact]
    public async Task Event_ids_are_unique_per_session_only()
    {
        var a = await _h.NewSessionAsync();
        var b = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        Assert.Equal(UploadOutcome.Accepted, (await _h.UploadAsync(a.Session.Id, a.Token, Meta(wav, id, Now), wav)).Outcome);
        // Another session using the same id is a different event, not a conflict (and reveals nothing).
        Assert.Equal(UploadOutcome.Accepted, (await _h.UploadAsync(b.Session.Id, b.Token, Meta(wav, id, Now), wav)).Outcome);
    }

    public static TheoryData<string, Action<JsonObject>, string> CrossFieldCases() => new()
    {
        { "size mismatch", m => m["audio"]!["sizeBytes"] = 12345, "audio.sizeBytes" },
        { "checksum mismatch", m => m["audio"]!["sha256"] = new string('0', 64), "audio.sha256" },
        { "duration mismatch", m => m["durationSeconds"] = 5.0, "durationSeconds" },
        { "speech outside clip", m => m["speechStartedAt"] = "2026-11-21T09:00:00.000Z", "speechStartedAt" },
        { "before the session", m => { m["startedAt"] = "2026-11-21T08:00:00.000Z"; m["endedAt"] = "2026-11-21T08:00:01.000Z"; m["speechStartedAt"] = "2026-11-21T08:00:00.500Z"; }, "startedAt" },
        { "video claimed uploaded", m => m["video"]!["uploaded"] = true, "video.uploaded" },
    };

    [Theory]
    [MemberData(nameof(CrossFieldCases))]
    public async Task Metadata_must_describe_the_uploaded_audio(string name, Action<JsonObject> change, string field)
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var meta = Meta(wav, Guid.NewGuid(), Now);
        change(meta);
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _h.UploadAsync(session.Id, token, meta, wav));
        Assert.True(ex.Errors.ContainsKey(field), $"{name}: {string.Join(",", ex.Errors.Keys)}");
        Assert.Empty(_h.StoredFiles());
        Assert.Empty(_h.StagingFiles());
    }

    [Fact]
    public async Task Clips_longer_than_the_policy_are_rejected()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(12); // policy: 10 s + 1 s tolerance
        var meta = Meta(wav, Guid.NewGuid(), Now);
        meta["durationSeconds"] = 12.0;
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _h.UploadAsync(session.Id, token, meta, wav));
        Assert.Contains("too long", ex.Errors["audio"][0]);
    }

    [Fact]
    public async Task Uploads_are_accepted_within_the_grace_period_after_the_end_then_refused()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var start = Now;
        _h.Clock.Advance(TimeSpan.FromSeconds(5));
        await using (var s = _h.NewScope()) await s.Sessions.EndAsync(session.Id, token, default);

        _h.Clock.Advance(TimeSpan.FromSeconds(60)); // inside SESSION_UPLOAD_GRACE_SECONDS (120)
        Assert.Equal(UploadOutcome.Accepted, (await _h.UploadAsync(session.Id, token, Meta(wav, Guid.NewGuid(), start), wav)).Outcome);

        _h.Clock.Advance(TimeSpan.FromSeconds(120));
        await Assert.ThrowsAsync<SessionNotAcceptingUploadsException>(() =>
            _h.UploadAsync(session.Id, token, Meta(wav, Guid.NewGuid(), start), wav));
    }

    [Fact]
    public async Task Expired_sessions_do_not_accept_uploads()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        _h.Clock.Advance(TimeSpan.FromHours(7)); // past max duration and grace
        var ex = await Assert.ThrowsAsync<SessionNotAcceptingUploadsException>(() =>
            _h.UploadAsync(session.Id, token, Meta(wav, Guid.NewGuid(), Now), wav));
        Assert.Contains("expired", ex.Message);
    }

    // ------------------------------------------------------------------ failures and recovery

    private sealed class FailingSaveRepository(IAudioEventRepository inner) : IAudioEventRepository
    {
        public Task<AudioEvent?> FindAsync(string s, Guid e, CancellationToken ct) => inner.FindAsync(s, e, ct);
        public Task<IReadOnlyList<AudioEvent>> ListAsync(string s, int m, CancellationToken ct) => inner.ListAsync(s, m, ct);
        public void Add(AudioEvent a) => inner.Add(a);
        public void Remove(AudioEvent a) => inner.Remove(a);
        public Task SaveChangesAsync(CancellationToken ct) => throw new InvalidOperationException("database is down");
        public Task<bool> TryClaimForPublishAsync(string s, Guid e, DateTime n, DateTime l, CancellationToken ct) => inner.TryClaimForPublishAsync(s, e, n, l, ct);
        public Task<bool> MarkQueuedAsync(string s, Guid e, DateTime n, CancellationToken ct) => inner.MarkQueuedAsync(s, e, n, ct);
        public Task ReleasePublishClaimAsync(string s, Guid e, CancellationToken ct) => inner.ReleasePublishClaimAsync(s, e, ct);
        public Task<IReadOnlyList<AudioEvent>> FindUnpublishedAsync(DateTime r, DateTime n, int m, CancellationToken ct) => inner.FindUnpublishedAsync(r, n, m, ct);
        public Task<IReadOnlyList<AudioEvent>> FindWithAudioReceivedBeforeAsync(DateTime r, int m, CancellationToken ct) => inner.FindWithAudioReceivedBeforeAsync(r, m, ct);
        public void DetachAll() => inner.DetachAll();
    }

    [Fact]
    public async Task A_database_failure_leaves_no_audio_file_behind()
    {
        var (session, token) = await _h.NewSessionAsync();
        _h.WrapRepository = r => new FailingSaveRepository(r);
        var wav = TestAudio.Wav(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.UploadAsync(session.Id, token, Meta(wav, Guid.NewGuid(), Now), wav));
        Assert.Empty(_h.StoredFiles());
        Assert.Empty(_h.StagingFiles());
        Assert.Empty(_h.Queue.Messages);
    }

    private sealed class FailingCommitStore(IAudioStore inner) : IAudioStore
    {
        public Task<StagedAudio> StageAsync(Stream c, long m, CancellationToken ct) => inner.StageAsync(c, m, ct);
        public string NewStorageKey(string s) => inner.NewStorageKey(s);
        public Task CommitAsync(StagedAudio s, string k, CancellationToken ct) => throw new IOException("disk full");
        public Task<bool> ExistsAsync(string k, CancellationToken ct) => inner.ExistsAsync(k, ct);
        public Task DeleteAsync(string k, CancellationToken ct) => inner.DeleteAsync(k, ct);
        public Uri GetProcessingUri(string k) => inner.GetProcessingUri(k);
        public Task<int> DeleteStaleStagingAsync(TimeSpan o, CancellationToken ct) => inner.DeleteStaleStagingAsync(o, ct);
    }

    [Fact]
    public async Task A_storage_failure_removes_the_event_record_again()
    {
        var (session, token) = await _h.NewSessionAsync();
        _h.WrapStore = s => new FailingCommitStore(s);
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => _h.UploadAsync(session.Id, token, Meta(wav, id, Now), wav));
        await using var db = _h.NewDb();
        Assert.Equal(0, await db.AudioEvents.CountAsync());
        Assert.Empty(_h.StagingFiles());
    }

    [Fact]
    public async Task Queue_outage_keeps_the_event_RECEIVED_and_maintenance_queues_it_later()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        _h.Queue.Fail = true;

        var result = await _h.UploadAsync(session.Id, token, Meta(wav, id, Now), wav);
        Assert.Equal(UploadOutcome.StoredQueueUnavailable, result.Outcome);
        Assert.Equal(AudioEventStatus.Received, result.Event.Status);
        Assert.Single(_h.StoredFiles()); // the audio is kept

        // Still down: maintenance tries and leaves it RECEIVED.
        _h.Clock.Advance(TimeSpan.FromSeconds(20));
        await using (var s = _h.NewScope()) Assert.Equal(0, (await s.Maintenance.RunOnceAsync(default)).Republished);
        Assert.Equal(AudioEventStatus.Received, (await _h.LoadAsync(session.Id, id)).Status);

        // Queue back: the next run queues it exactly once.
        _h.Queue.Fail = false;
        _h.Clock.Advance(TimeSpan.FromSeconds(20));
        await using (var s = _h.NewScope()) Assert.Equal(1, (await s.Maintenance.RunOnceAsync(default)).Republished);
        await using (var s = _h.NewScope()) Assert.Equal(0, (await s.Maintenance.RunOnceAsync(default)).Republished);
        Assert.Equal(AudioEventStatus.Queued, (await _h.LoadAsync(session.Id, id)).Status);
        Assert.Single(_h.Queue.Messages);
    }

    [Fact]
    public async Task A_retry_during_a_queue_outage_returns_the_stored_event()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var meta = Meta(wav, Guid.NewGuid(), Now);
        _h.Queue.Fail = true;
        await _h.UploadAsync(session.Id, token, meta, wav);
        var retry = await _h.UploadAsync(session.Id, token, meta, wav);
        Assert.Equal(UploadOutcome.Duplicate, retry.Outcome);
        Assert.Equal(AudioEventStatus.Received, retry.Event.Status);
        _h.Queue.Fail = false;
        var third = await _h.UploadAsync(session.Id, token, meta, wav);
        Assert.Equal(AudioEventStatus.Queued, third.Event.Status);
        Assert.Single(_h.Queue.Messages);
    }

    /// <summary>Simulates a crash after the database write but before the file was moved into place.</summary>
    private async Task<(Session Session, string Token, Guid Id, byte[] Wav, JsonObject Meta)> CrashBetweenDatabaseAndFileAsync()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        var meta = Meta(wav, id, Now);
        await _h.UploadAsync(session.Id, token, meta, wav);
        var stored = await _h.LoadAsync(session.Id, id);
        await using (var db = _h.NewDb())
            await db.AudioEvents.Where(e => e.EventId == id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, AudioEventStatus.Received).SetProperty(e => e.QueuedAt, (DateTime?)null));
        File.Delete(Path.Combine(_h.StoreRoot, stored.StorageKey!));
        return (session, token, id, wav, meta);
    }

    [Fact]
    public async Task A_retried_upload_repairs_an_event_whose_file_was_never_stored()
    {
        var (session, token, id, wav, meta) = await CrashBetweenDatabaseAndFileAsync();

        // A retry while the row is young does not touch the file (it may still be in flight).
        var early = await _h.UploadAsync(session.Id, token, meta, wav);
        Assert.Equal(AudioEventStatus.Received, early.Event.Status);
        Assert.Empty(_h.StoredFiles());

        _h.Clock.Advance(TimeSpan.FromSeconds(31)); // older than RepairAfterSeconds: abandoned
        var retry = await _h.UploadAsync(session.Id, token, meta, wav);
        Assert.Equal(UploadOutcome.Duplicate, retry.Outcome);
        Assert.Equal(AudioEventStatus.Queued, retry.Event.Status);
        Assert.Equal(wav, await File.ReadAllBytesAsync(Assert.Single(_h.StoredFiles())));
    }

    [Fact]
    public async Task Without_a_retry_maintenance_marks_the_event_failed_not_suspicious()
    {
        var (session, _, id, _, _) = await CrashBetweenDatabaseAndFileAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(11));
        await using (var s = _h.NewScope()) Assert.Equal(1, (await s.Maintenance.RunOnceAsync(default)).MarkedMissing);
        var failed = await _h.LoadAsync(session.Id, id);
        Assert.Equal(AudioEventStatus.Failed, failed.Status);
        Assert.Equal("AUDIO_FILE_MISSING", failed.ErrorCode);
    }

    [Fact]
    public async Task Retention_deletes_audio_but_keeps_the_event_record()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        await _h.UploadAsync(session.Id, token, Meta(wav, id, Now), wav);
        _h.Clock.Advance(TimeSpan.FromHours(25));

        await using (var s = _h.NewScope()) Assert.Equal(1, (await s.Maintenance.RunOnceAsync(default)).AudioFilesExpired);

        Assert.Empty(_h.StoredFiles());
        var e = await _h.LoadAsync(session.Id, id);
        Assert.Null(e.StorageKey);
        Assert.NotNull(e.AudioDeletedAt);
        Assert.Equal(AudioEventStatus.Failed, e.Status); // never analysed (no consumer yet)
        Assert.Equal("AUDIO_RETENTION_EXPIRED", e.ErrorCode);
        Assert.Equal(TestAudio.Sha256(wav), e.AudioSha256); // facts about the clip remain
    }

    [Fact]
    public async Task Events_of_one_session_are_invisible_to_another()
    {
        var a = await _h.NewSessionAsync();
        var b = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        await _h.UploadAsync(a.Session.Id, a.Token, Meta(wav, id, Now), wav);

        await using var s = _h.NewScope();
        await Assert.ThrowsAsync<EventNotFoundException>(() => s.Events.GetAsync(b.Session.Id, b.Token, id, default));
        await Assert.ThrowsAsync<SessionNotFoundException>(() => s.Events.GetAsync(a.Session.Id, b.Token, id, default));
        Assert.Empty(await s.Events.ListAsync(b.Session.Id, b.Token, default));
    }
}
