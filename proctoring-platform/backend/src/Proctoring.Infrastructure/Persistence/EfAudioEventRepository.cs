using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Proctoring.Application.Events;
using Proctoring.Domain.Events;

namespace Proctoring.Infrastructure.Persistence;

public sealed class EfAudioEventRepository(ProctoringDbContext db, IEventUpdateNotifier notifier) : IAudioEventRepository
{
    public Task<AudioEvent?> FindAsync(string sessionId, Guid eventId, CancellationToken ct) =>
        db.AudioEvents.FirstOrDefaultAsync(e => e.SessionId == sessionId && e.EventId == eventId, ct);

    public async Task<IReadOnlyList<AudioEvent>> ListAsync(string sessionId, int max, CancellationToken ct) =>
        await db.AudioEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.SequenceNumber).ThenByDescending(e => e.ReceivedAt)
            .Take(max)
            .ToListAsync(ct);

    public void Add(AudioEvent audioEvent) => db.AudioEvents.Add(audioEvent);

    public void Remove(AudioEvent audioEvent) => db.AudioEvents.Remove(audioEvent);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite
                                           && sqlite.Message.Contains("audio_events", StringComparison.Ordinal))
        {
            throw new DuplicateEventException(ex);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new EventConcurrencyException(ex);
        }
        foreach (var session in db.LastChangedSessions) notifier.Notify(session);
    }

    // The conditional updates below run as single SQL statements, so the WHERE clause decides atomically
    // which concurrent caller wins (no read-then-write race).

    public async Task<bool> TryClaimForPublishAsync(string sessionId, Guid eventId, DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct) =>
        await db.AudioEvents
            .Where(e => e.SessionId == sessionId && e.EventId == eventId && e.Status == AudioEventStatus.Received
                        && (e.PublishLeaseUntil == null || e.PublishLeaseUntil < nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PublishLeaseUntil, leaseUntilUtc), ct) == 1;

    public async Task<bool> MarkQueuedAsync(string sessionId, Guid eventId, DateTime nowUtc, CancellationToken ct)
    {
        // Status change and change-log entry in one transaction (see ProctoringDbContext).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.AudioEvents
            .Where(e => e.SessionId == sessionId && e.EventId == eventId && e.Status == AudioEventStatus.Received)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, AudioEventStatus.Queued)
                .SetProperty(e => e.QueuedAt, nowUtc)
                .SetProperty(e => e.UpdatedAt, nowUtc)
                .SetProperty(e => e.PublishLeaseUntil, (DateTime?)null)
                .SetProperty(e => EF.Property<long>(e, ProctoringDbContext.VersionProperty), e => EF.Property<long>(e, ProctoringDbContext.VersionProperty) + 1), ct) == 1;
        if (updated)
        {
            db.EventUpdates.Add(EventUpdate.For(sessionId, eventId, AudioEventStatus.Queued, nowUtc));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (updated) notifier.Notify(sessionId);
        await ReloadIfTrackedAsync(sessionId, eventId, ct);
        return updated;
    }

    public async Task ReleasePublishClaimAsync(string sessionId, Guid eventId, CancellationToken ct)
    {
        await db.AudioEvents
            .Where(e => e.SessionId == sessionId && e.EventId == eventId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PublishLeaseUntil, (DateTime?)null), ct);
        await ReloadIfTrackedAsync(sessionId, eventId, ct);
    }

    public async Task<IReadOnlyList<AudioEvent>> FindUnpublishedAsync(DateTime receivedBeforeUtc, DateTime nowUtc, int max, CancellationToken ct) =>
        await db.AudioEvents
            .Where(e => e.Status == AudioEventStatus.Received && e.ReceivedAt < receivedBeforeUtc
                        && (e.PublishLeaseUntil == null || e.PublishLeaseUntil < nowUtc))
            .OrderBy(e => e.ReceivedAt)
            .Take(max)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AudioEvent>> FindWithAudioReceivedBeforeAsync(DateTime receivedBeforeUtc, int max, CancellationToken ct) =>
        await db.AudioEvents
            .Where(e => e.StorageKey != null && e.ReceivedAt < receivedBeforeUtc)
            .OrderBy(e => e.ReceivedAt)
            .Take(max)
            .ToListAsync(ct);

    public void DetachAll() => db.ChangeTracker.Clear();

    /// <summary>ExecuteUpdate bypasses the change tracker; refresh a tracked copy so callers see the new state.</summary>
    private async Task ReloadIfTrackedAsync(string sessionId, Guid eventId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<AudioEvent>()
            .FirstOrDefault(x => x.Entity.SessionId == sessionId && x.Entity.EventId == eventId);
        if (tracked is not null) await tracked.ReloadAsync(ct);
    }
}
