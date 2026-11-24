using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proctoring.Application.Errors;
using Proctoring.Contracts.Sessions;
using Proctoring.Domain.Sessions;

namespace Proctoring.Application.Sessions;

public sealed record CreateSessionResult(Session Session, string Token, bool Replayed);

/// <summary>
/// Session use cases: create, read, end and authorize. HTTP-agnostic; the API layer maps the
/// exceptions in Proctoring.Application.Errors to status codes.
/// </summary>
public sealed class SessionService(
    ISessionRepository repository,
    ISessionTokenService tokens,
    ISessionIdGenerator ids,
    TimeProvider clock,
    IOptions<SessionOptions> options,
    ILogger<SessionService> logger)
{
    private readonly SessionOptions _options = options.Value;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates a session.
    ///
    /// Without an idempotency key the token is 256 random bits. With a key, the token is derived from a
    /// random per-session salt and the server secret (HMAC). A retry with the same key and body inside the
    /// replay window therefore returns the SAME session and the SAME token: a client whose first response was
    /// lost gets the token it never saw, and a client already using the token is not affected.
    /// </summary>
    public async Task<CreateSessionResult> CreateAsync(CreateSessionRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is null)
        {
            var (randomToken, randomHash) = tokens.Generate();
            var plain = Session.Start(ids.NewId(), request.CandidateId, request.ExamId, randomHash, Now, _options.MaxDuration,
                request.Client?.UserAgent, request.Client?.AppVersion);
            repository.Add(plain);
            await repository.SaveChangesAsync(ct);
            logger.LogInformation("Session {SessionId} created for exam {ExamId}", plain.Id, plain.ExamId);
            return new CreateSessionResult(plain, randomToken, Replayed: false);
        }

        var keyHash = HashKey(idempotencyKey);
        var fingerprint = Fingerprint(request);
        var existing = await repository.FindByIdempotencyKeyHashAsync(keyHash, ct);
        if (existing is not null) return await ReplayAsync(existing, fingerprint, ct);

        var id = ids.NewId();
        var salt = tokens.NewDerivationSalt();
        var token = tokens.Derive(id, salt);
        var session = Session.Start(id, request.CandidateId, request.ExamId, tokens.Hash(token), Now, _options.MaxDuration,
            request.Client?.UserAgent, request.Client?.AppVersion);
        session.EnableIdempotentReplay(keyHash, fingerprint, salt, Now + _options.IdempotencyWindow);
        repository.Add(session);
        try
        {
            await repository.SaveChangesAsync(ct);
        }
        catch (DuplicateIdempotencyKeyException)
        {
            // A concurrent request with the same key won the race: return its session and token.
            var winner = await repository.FindByIdempotencyKeyHashAsync(keyHash, ct)
                         ?? throw new InvalidOperationException("Idempotency key conflict without a stored session");
            return await ReplayAsync(winner, fingerprint, ct);
        }

        logger.LogInformation("Session {SessionId} created for exam {ExamId} (idempotent)", session.Id, session.ExamId);
        return new CreateSessionResult(session, token, Replayed: false);
    }

    /// <summary>Returns the session if <paramref name="presentedToken"/> belongs to it.</summary>
    public Task<Session> GetAsync(string sessionId, string presentedToken, CancellationToken ct) =>
        AuthorizeAsync(sessionId, presentedToken, ct);

    /// <summary>Ends the session. Idempotent: ending an ended session returns it unchanged.</summary>
    public async Task<Session> EndAsync(string sessionId, string presentedToken, CancellationToken ct)
    {
        var session = await AuthorizeAsync(sessionId, presentedToken, ct);
        if (session.End(SessionEndReason.Candidate, Now))
        {
            await repository.SaveChangesAsync(ct);
            logger.LogInformation("Session {SessionId} ended by candidate", session.Id);
        }
        return session;
    }

    /// <summary>
    /// Loads the session and checks the token. Unknown session and wrong token both raise
    /// <see cref="SessionNotFoundException"/>. Also applies automatic expiry and erases the token
    /// derivation salt once the replay window has passed.
    /// </summary>
    public async Task<Session> AuthorizeAsync(string sessionId, string presentedToken, CancellationToken ct)
    {
        var session = await repository.FindAsync(sessionId, ct);
        // Verify even when the session is unknown (against a dummy hash) so both cases take similar time.
        var tokenMatches = tokens.Verify(presentedToken, session?.TokenHash ?? DummyHash);
        if (session is null || !tokenMatches) throw new SessionNotFoundException();

        var changed = session.ExpireIfDue(Now);
        changed |= session.ForgetTokenDerivationIfNotReplayable(Now);
        if (changed) await repository.SaveChangesAsync(ct);
        if (session.Status == SessionStatus.Ended && session.EndReason == SessionEndReason.Expired && changed)
            logger.LogInformation("Session {SessionId} expired", session.Id);

        if (!session.IsTokenUsableAt(Now, _options.TokenTtlAfterEnd)) throw new TokenExpiredException();
        return session;
    }

    private async Task<CreateSessionResult> ReplayAsync(Session existing, byte[] fingerprint, CancellationToken ct)
    {
        if (existing.RequestFingerprint is null || !CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint, fingerprint))
            throw new IdempotencyConflictException();

        var now = Now;
        var changed = existing.ExpireIfDue(now);
        if (!existing.CanReplayCreate(now))
        {
            changed |= existing.ForgetTokenDerivationIfNotReplayable(now);
            if (changed) await repository.SaveChangesAsync(ct);
            var reason = existing.Status == SessionStatus.Ended ? "the session has ended" : "the replay window has passed";
            throw new IdempotentReplayUnavailableException(reason);
        }

        var token = tokens.Derive(existing.Id, existing.TokenDerivationSalt!);
        if (!tokens.Verify(token, existing.TokenHash))
        {
            // The server secret changed since the session was created; the token cannot be recreated.
            throw new IdempotentReplayUnavailableException("the server secret has changed");
        }
        if (changed) await repository.SaveChangesAsync(ct);
        logger.LogInformation("Session {SessionId} returned for a repeated create request", existing.Id);
        return new CreateSessionResult(existing, token, Replayed: true);
    }

    private static readonly byte[] DummyHash = new byte[Session.TokenHashLength];

    /// <summary>The idempotency key can recover a token during the window, so it is stored only as a hash.</summary>
    internal static byte[] HashKey(string idempotencyKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("proctoring.idempotency-key.v1\n" + idempotencyKey));

    /// <summary>Identifies "the same request" for idempotency: the fields that define the session.</summary>
    internal static byte[] Fingerprint(CreateSessionRequest r) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{r.CandidateId}\n{r.ExamId}\n{r.Client?.UserAgent}\n{r.Client?.AppVersion}"));
}
