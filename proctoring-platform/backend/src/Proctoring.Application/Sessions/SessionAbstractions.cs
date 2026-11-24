using Proctoring.Domain.Sessions;

namespace Proctoring.Application.Sessions;

/// <summary>Persistence for sessions (implemented with EF Core + SQLite in Infrastructure).</summary>
public interface ISessionRepository
{
    Task<Session?> FindAsync(string sessionId, CancellationToken ct);
    Task<Session?> FindByIdempotencyKeyHashAsync(byte[] idempotencyKeyHash, CancellationToken ct);
    void Add(Session session);

    /// <summary>Saves changes. Throws <see cref="DuplicateIdempotencyKeyException"/> on a unique-key race.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Creates and checks session tokens. Only hashes are ever stored.</summary>
public interface ISessionTokenService
{
    /// <summary>New random token (returned to the client once) and its hash (stored).</summary>
    (string Token, byte[] Hash) Generate();

    /// <summary>Random salt for a token that must be re-derivable during the idempotency window.</summary>
    byte[] NewDerivationSalt();

    /// <summary>
    /// Deterministic token for (session, salt) using the server secret. Same inputs and secret give the same
    /// token, which is how a retried create returns the token the client may already be using.
    /// </summary>
    string Derive(string sessionId, byte[] derivationSalt);

    /// <summary>SHA-256 of a token (what is stored).</summary>
    byte[] Hash(string token);

    /// <summary>Constant-time check of a presented token against a stored hash.</summary>
    bool Verify(string presentedToken, byte[] storedHash);
}

public interface ISessionIdGenerator
{
    string NewId();
}

public sealed class DuplicateIdempotencyKeyException(Exception inner)
    : Exception("A session with this idempotency key was created concurrently", inner);
