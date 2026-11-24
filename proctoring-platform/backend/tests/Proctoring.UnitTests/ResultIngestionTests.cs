using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Proctoring.Application.Events;
using Proctoring.Domain.Events;
using Proctoring.Infrastructure.Notifications;
using Proctoring.Infrastructure.Persistence;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;

namespace Proctoring.UnitTests;

/// <summary>
/// Result messages from the processing service: validation, idempotency, final states, concurrency,
/// database failures, and the change log that feeds Server-Sent Events.
/// </summary>
public sealed class ResultIngestionTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Contracts = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private readonly EventHarness _h = new();
    public void Dispose() => _h.Dispose();

    private DateTime Now => _h.Clock.GetUtcNow().UtcDateTime;

    private async Task<(string SessionId, Guid EventId)> QueuedEventAsync()
    {
        var (session, token) = await _h.NewSessionAsync();
        var wav = TestAudio.Wav(2);
        var id = Guid.NewGuid();
        var result = await _h.UploadAsync(session.Id, token, TestAudio.Metadata(wav, id, Now), wav);
        Assert.Equal(AudioEventStatus.Queued, result.Event.Status);
        return (session.Id, id);
    }

    private ResultIngestionService Service(ProctoringDbContext db, Func<IAudioEventRepository, IAudioEventRepository>? wrap = null)
    {
        IAudioEventRepository repo = new EfAudioEventRepository(db, new InProcessEventUpdateNotifier());
        if (wrap is not null) repo = wrap(repo);
        return new ResultIngestionService(repo, Contracts, _h.Clock, NullLogger<ResultIngestionService>.Instance);
    }

    private async Task<ProcessingOutcome> ApplyAsync(JsonObject payload, int deliveryCount = 1, string? type = null,
        string version = "1", Func<IAudioEventRepository, IAudioEventRepository>? wrap = null, string? envelopeEventId = null)
    {
        await using var db = _h.NewDb();
        var message = new ProcessingMessage(type ?? TestMessages.TypeOf(payload), version, Guid.NewGuid().ToString(),
            (string)payload["sessionId"]!, envelopeEventId ?? (string)payload["eventId"]!, payload.ToJsonString());
        return await Service(db, wrap).ApplyAsync(message, deliveryCount, default);
    }

    private async Task<List<EventUpdate>> UpdatesAsync(string sessionId)
    {
        await using var db = _h.NewDb();
        return await db.EventUpdates.Where(u => u.SessionId == sessionId).OrderBy(u => u.Id).ToListAsync();
    }

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public async Task Started_then_completed_moves_QUEUED_to_PROCESSING_to_COMPLETED_and_stores_the_result()
    {
        var (sid, id) = await QueuedEventAsync();

        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Started(sid, id))).Disposition);
        Assert.Equal(AudioEventStatus.Processing, (await _h.LoadAsync(sid, id)).Status);

        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Completed(sid, id, "first result"))).Disposition);
        var e = await _h.LoadAsync(sid, id);
        Assert.Equal(AudioEventStatus.Completed, e.Status);
        Assert.Equal("first result", (string?)JsonNode.Parse(e.ResultJson!)!["transcription"]!["text"]);
        Assert.Null(e.ErrorCode);
    }

    [Fact]
    public async Task Duplicate_and_late_messages_never_overwrite_a_completed_result()
    {
        var (sid, id) = await QueuedEventAsync();
        await ApplyAsync(TestMessages.Completed(sid, id, "first result"));
        var updatesBefore = (await UpdatesAsync(sid)).Count;

        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(TestMessages.Completed(sid, id, "second result"), 2)).Disposition);
        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(TestMessages.Started(sid, id, 3), 3)).Disposition);
        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(TestMessages.Failed(sid, id, "DEAD_LETTERED"))).Disposition);

        var e = await _h.LoadAsync(sid, id);
        Assert.Equal(AudioEventStatus.Completed, e.Status);
        Assert.Equal("first result", (string?)JsonNode.Parse(e.ResultJson!)!["transcription"]!["text"]);
        Assert.Equal(updatesBefore, (await UpdatesAsync(sid)).Count); // ignored messages leave no change-log entry
    }

    [Fact]
    public async Task Retryable_failure_is_FAILED_and_a_later_success_completes_the_event()
    {
        var (sid, id) = await QueuedEventAsync();
        await ApplyAsync(TestMessages.Started(sid, id));
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Failed(sid, id, "ASR_TIMEOUT", retryable: true))).Disposition);
        var failed = await _h.LoadAsync(sid, id);
        Assert.Equal(AudioEventStatus.Failed, failed.Status);
        Assert.Equal("ASR_TIMEOUT", failed.ErrorCode);

        // Exact duplicate of the failure: nothing changes.
        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(TestMessages.Failed(sid, id, "ASR_TIMEOUT", retryable: true))).Disposition);
        // A late "started" of the failed attempt does not reopen it; a new attempt does.
        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(TestMessages.Started(sid, id, 1))).Disposition);
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Started(sid, id, 2), 2)).Disposition);
        Assert.Equal(AudioEventStatus.Processing, (await _h.LoadAsync(sid, id)).Status);

        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Completed(sid, id), 2)).Disposition);
        var done = await _h.LoadAsync(sid, id);
        Assert.Equal(AudioEventStatus.Completed, done.Status);
        Assert.Null(done.ErrorCode);
        Assert.Equal(2, done.Attempts);
    }

    [Fact]
    public async Task DEAD_LETTERED_failure_is_final()
    {
        var (sid, id) = await QueuedEventAsync();
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Failed(sid, id, ResultIngestionService.DeadLetteredCode, deliveryCount: 5), 5)).Disposition);
        var e = await _h.LoadAsync(sid, id);
        Assert.Equal(AudioEventStatus.DeadLettered, e.Status);
        Assert.Equal(5, e.Attempts);

        Assert.Equal(ProcessingDisposition.Ignored, (await ApplyAsync(TestMessages.Completed(sid, id))).Disposition);
        Assert.Equal(AudioEventStatus.DeadLettered, (await _h.LoadAsync(sid, id)).Status);
    }

    [Fact]
    public async Task Result_for_an_event_still_RECEIVED_records_QUEUED_first()
    {
        // Crash between XADD and "mark QUEUED": the processor may answer before the backend recorded QUEUED.
        var (sid, id) = await QueuedEventAsync();
        await using (var db = _h.NewDb())
            await db.AudioEvents.Where(e => e.EventId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, AudioEventStatus.Received));

        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Started(sid, id))).Disposition);
        var e = await _h.LoadAsync(sid, id);
        Assert.Equal(AudioEventStatus.Processing, e.Status);
        Assert.NotNull(e.QueuedAt);
    }

    // ------------------------------------------------------------------ poison messages

    [Fact]
    public async Task Invalid_messages_are_dead_lettered_and_change_nothing()
    {
        var (sid, id) = await QueuedEventAsync();
        var bad = TestMessages.Completed(sid, id);
        bad.Remove("findings");

        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(bad)).Disposition);
        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(TestMessages.Completed(sid, id), version: "2")).Disposition);
        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(TestMessages.Completed(sid, id), type: "audio.analysis.unknown")).Disposition);
        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(TestMessages.Completed(sid, id), type: ResultIngestionService.FailedType)).Disposition);
        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(TestMessages.Completed(sid, id), envelopeEventId: Guid.NewGuid().ToString())).Disposition);
        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(TestMessages.Completed(sid, Guid.NewGuid()))).Disposition); // unknown event
        Assert.Equal(ProcessingDisposition.DeadLetter, (await ApplyAsync(TestMessages.Completed("ses_UNKNOWNUNKNOWNUNKNOWN1", id))).Disposition); // other session

        await using var db = _h.NewDb();
        var message = new ProcessingMessage(ResultIngestionService.CompletedType, "1", Guid.NewGuid().ToString(), sid, id.ToString(), "{not json");
        Assert.Equal(ProcessingDisposition.DeadLetter, (await Service(db).ApplyAsync(message, 1, default)).Disposition);

        Assert.Equal(AudioEventStatus.Queued, (await _h.LoadAsync(sid, id)).Status);
    }

    // ------------------------------------------------------------------ failures and concurrency

    [Fact]
    public async Task Database_failure_returns_Retry_so_the_message_is_not_acknowledged()
    {
        var (sid, id) = await QueuedEventAsync();
        var outcome = await ApplyAsync(TestMessages.Completed(sid, id), wrap: r => new FailingSaveRepository(r));
        Assert.Equal(ProcessingDisposition.Retry, outcome.Disposition);
        Assert.Equal(AudioEventStatus.Queued, (await _h.LoadAsync(sid, id)).Status);

        // Redelivery after the database is back applies it.
        Assert.Equal(ProcessingDisposition.Applied, (await ApplyAsync(TestMessages.Completed(sid, id), 2)).Disposition);
    }

    [Fact]
    public async Task Concurrent_writer_wins_are_detected_and_the_message_is_re_evaluated()
    {
        var (sid, id) = await QueuedEventAsync();
        // While "started" is being applied, another instance completes the event (between our read and our save).
        var outcome = await ApplyAsync(TestMessages.Started(sid, id), wrap: r => new InterleavingRepository(r, async () =>
        {
            await using var other = _h.NewDb();
            var applied = await Service(other).ApplyAsync(new ProcessingMessage(ResultIngestionService.CompletedType, "1",
                Guid.NewGuid().ToString(), sid, id.ToString(), TestMessages.Completed(sid, id, "winner").ToJsonString()), 1, default);
            Assert.Equal(ProcessingDisposition.Applied, applied.Disposition);
        }));

        Assert.Equal(ProcessingDisposition.Ignored, outcome.Disposition);
        var e = await _h.LoadAsync(sid, id);
        Assert.Equal(AudioEventStatus.Completed, e.Status);
        Assert.Equal("winner", (string?)JsonNode.Parse(e.ResultJson!)!["transcription"]!["text"]);
    }

    // ------------------------------------------------------------------ change log

    [Fact]
    public async Task Every_status_change_appends_exactly_one_change_log_entry_in_order()
    {
        var (sid, id) = await QueuedEventAsync();
        await ApplyAsync(TestMessages.Started(sid, id));
        await ApplyAsync(TestMessages.Completed(sid, id));

        var updates = await UpdatesAsync(sid);
        Assert.Equal([AudioEventStatus.Received, AudioEventStatus.Queued, AudioEventStatus.Processing, AudioEventStatus.Completed],
            updates.Select(u => u.Status));
        Assert.All(updates, u => Assert.Equal(id, u.EventId));
        Assert.Equal(updates.Select(u => u.Id).Order(), updates.Select(u => u.Id));
    }

    [Fact]
    public async Task Publish_lease_changes_do_not_create_change_log_entries()
    {
        var (sid, id) = await QueuedEventAsync();
        var before = (await UpdatesAsync(sid)).Count;
        await using (var db = _h.NewDb())
        {
            var repo = new EfAudioEventRepository(db, new InProcessEventUpdateNotifier());
            await repo.ReleasePublishClaimAsync(sid, id, default);
            await repo.TryClaimForPublishAsync(sid, id, Now, Now.AddSeconds(30), default); // not RECEIVED: no-op
        }
        Assert.Equal(before, (await UpdatesAsync(sid)).Count);
    }

    [Fact]
    public async Task Change_log_pruning_removes_only_old_entries()
    {
        var (sid, _) = await QueuedEventAsync();
        _h.Clock.Advance(TimeSpan.FromHours(30));
        await QueuedEventAsync();
        await using var db = _h.NewDb();
        var repo = new EfEventUpdateRepository(db);
        var deleted = await repo.DeleteOlderThanAsync(Now.AddHours(-24), default);
        Assert.Equal(2, deleted); // RECEIVED + QUEUED of the first event
        Assert.Equal(0, await repo.GetLatestIdAsync(sid, default));
        Assert.True(await repo.GetOldestRetainedIdAsync(default) > 2);
    }

    // ------------------------------------------------------------------ notifier

    [Fact]
    public async Task In_process_notifier_wakes_waiters_of_the_notified_session_only()
    {
        var notifier = new InProcessEventUpdateNotifier();
        var a = notifier.WaitAsync("ses_a", TimeSpan.FromSeconds(10), default);
        var b = notifier.WaitAsync("ses_b", TimeSpan.FromMilliseconds(200), default);
        notifier.Notify("ses_a");
        await a.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(b.IsCompleted);
        await b; // times out quietly
        notifier.Notify("ses_nobody"); // no waiter: no-op
    }

    [Fact]
    public async Task In_process_notifier_wait_honours_cancellation()
    {
        var notifier = new InProcessEventUpdateNotifier();
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notifier.WaitAsync("ses_x", TimeSpan.FromSeconds(30), cts.Token));
    }

    // ------------------------------------------------------------------ doubles

    private sealed class FailingSaveRepository(IAudioEventRepository inner) : DelegatingRepository(inner)
    {
        public override Task SaveChangesAsync(CancellationToken ct) => throw new IOException("database is locked");
    }

    private sealed class InterleavingRepository(IAudioEventRepository inner, Func<Task> beforeFirstSave) : DelegatingRepository(inner)
    {
        private bool _done;
        public override async Task SaveChangesAsync(CancellationToken ct)
        {
            if (!_done)
            {
                _done = true;
                await beforeFirstSave();
            }
            await base.SaveChangesAsync(ct);
        }
    }
}

/// <summary>Forwards everything; override what a test needs to break.</summary>
public abstract class DelegatingRepository(IAudioEventRepository inner) : IAudioEventRepository
{
    public Task<AudioEvent?> FindAsync(string sessionId, Guid eventId, CancellationToken ct) => inner.FindAsync(sessionId, eventId, ct);
    public Task<IReadOnlyList<AudioEvent>> ListAsync(string sessionId, int max, CancellationToken ct) => inner.ListAsync(sessionId, max, ct);
    public void Add(AudioEvent audioEvent) => inner.Add(audioEvent);
    public void Remove(AudioEvent audioEvent) => inner.Remove(audioEvent);
    public virtual Task SaveChangesAsync(CancellationToken ct) => inner.SaveChangesAsync(ct);
    public Task<bool> TryClaimForPublishAsync(string sessionId, Guid eventId, DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct) =>
        inner.TryClaimForPublishAsync(sessionId, eventId, nowUtc, leaseUntilUtc, ct);
    public Task<bool> MarkQueuedAsync(string sessionId, Guid eventId, DateTime nowUtc, CancellationToken ct) => inner.MarkQueuedAsync(sessionId, eventId, nowUtc, ct);
    public Task ReleasePublishClaimAsync(string sessionId, Guid eventId, CancellationToken ct) => inner.ReleasePublishClaimAsync(sessionId, eventId, ct);
    public Task<IReadOnlyList<AudioEvent>> FindUnpublishedAsync(DateTime receivedBeforeUtc, DateTime nowUtc, int max, CancellationToken ct) =>
        inner.FindUnpublishedAsync(receivedBeforeUtc, nowUtc, max, ct);
    public Task<IReadOnlyList<AudioEvent>> FindWithAudioReceivedBeforeAsync(DateTime receivedBeforeUtc, int max, CancellationToken ct) =>
        inner.FindWithAudioReceivedBeforeAsync(receivedBeforeUtc, max, ct);
    public void DetachAll() => inner.DetachAll();
}
