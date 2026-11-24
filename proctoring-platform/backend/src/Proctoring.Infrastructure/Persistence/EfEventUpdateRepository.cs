using Microsoft.EntityFrameworkCore;
using Proctoring.Application.Events;
using Proctoring.Domain.Events;

namespace Proctoring.Infrastructure.Persistence;

public sealed class EfEventUpdateRepository(ProctoringDbContext db) : IEventUpdateRepository
{
    public async Task<IReadOnlyList<EventUpdate>> GetAfterAsync(string sessionId, long afterId, int max, CancellationToken ct) =>
        await db.EventUpdates.AsNoTracking()
            .Where(u => u.SessionId == sessionId && u.Id > afterId)
            .OrderBy(u => u.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<long> GetLatestIdAsync(string sessionId, CancellationToken ct) =>
        await db.EventUpdates.Where(u => u.SessionId == sessionId).MaxAsync(u => (long?)u.Id, ct) ?? 0;

    public async Task<long> GetOldestRetainedIdAsync(CancellationToken ct) =>
        await db.EventUpdates.MinAsync(u => (long?)u.Id, ct) ?? 0;

    public Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct) =>
        db.EventUpdates.Where(u => u.CreatedAt < cutoffUtc).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, AudioEvent>> GetEventsAsync(string sessionId, IReadOnlyCollection<Guid> eventIds, CancellationToken ct) =>
        await db.AudioEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId && eventIds.Contains(e.EventId))
            .ToDictionaryAsync(e => e.EventId, ct);
}
