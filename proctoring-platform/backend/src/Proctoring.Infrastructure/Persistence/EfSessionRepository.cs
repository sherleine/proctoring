using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Proctoring.Application.Sessions;
using Proctoring.Domain.Sessions;

namespace Proctoring.Infrastructure.Persistence;

public sealed class EfSessionRepository(ProctoringDbContext db) : ISessionRepository
{
    public Task<Session?> FindAsync(string sessionId, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public Task<Session?> FindByIdempotencyKeyHashAsync(byte[] idempotencyKeyHash, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.IdempotencyKeyHash == idempotencyKeyHash, ct);

    public void Add(Session session) => db.Sessions.Add(session);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite // SQLITE_CONSTRAINT
                                           && sqlite.Message.Contains("idempotency_key_hash", StringComparison.Ordinal))
        {
            // Forget the losing insert so the context can be reused for the replay.
            foreach (var entry in ex.Entries) entry.State = EntityState.Detached;
            throw new DuplicateIdempotencyKeyException(ex);
        }
    }
}
