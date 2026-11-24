namespace Proctoring.Domain.Sessions;

public enum SessionStatus
{
    Active,
    Ended,
}

public enum SessionEndReason
{
    /// <summary>The client called POST /sessions/{id}/end.</summary>
    Candidate,
    /// <summary>The session reached its maximum duration.</summary>
    Expired,
}

/// <summary>
/// One monitored exam attempt. Never holds a plaintext session token or idempotency key: only their
/// SHA-256 hashes, plus (for idempotent creates, temporarily) the random salt from which the
/// server can re-derive the same token for a retried create request.
/// </summary>
public sealed class Session
{
    public const int HashLength = 32;
    public const int TokenHashLength = HashLength;
    public const int DerivationSaltLength = 32;

    public string Id { get; private set; } = default!;
    public string CandidateId { get; private set; } = default!;
    public string ExamId { get; private set; } = default!;
    public SessionStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public SessionEndReason? EndReason { get; private set; }
    public byte[] TokenHash { get; private set; } = default!;
    public string? ClientUserAgent { get; private set; }
    public string? ClientAppVersion { get; private set; }

    /// <summary>SHA-256 of the client's Idempotency-Key (the key itself is not stored).</summary>
    public byte[]? IdempotencyKeyHash { get; private set; }

    /// <summary>Hash of the create request, to detect the same key reused for a different request.</summary>
    public byte[]? RequestFingerprint { get; private set; }

    /// <summary>
    /// Random salt used with the server secret to derive the token of an idempotent create.
    /// Kept only while a replay is allowed, then erased so the token can no longer be re-derived.
    /// </summary>
    public byte[]? TokenDerivationSalt { get; private set; }

    /// <summary>Until when a retried create request may return this session and its token.</summary>
    public DateTime? IdempotencyExpiresAt { get; private set; }

    private Session() { } // for EF Core

    public static Session Start(
        string id,
        string candidateId,
        string examId,
        byte[] tokenHash,
        DateTime nowUtc,
        TimeSpan maxDuration,
        string? clientUserAgent = null,
        string? clientAppVersion = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Session id is required", nameof(id));
        if (string.IsNullOrWhiteSpace(candidateId)) throw new ArgumentException("Candidate id is required", nameof(candidateId));
        if (string.IsNullOrWhiteSpace(examId)) throw new ArgumentException("Exam id is required", nameof(examId));
        EnsureLength(tokenHash, HashLength, nameof(tokenHash));
        if (maxDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxDuration));
        EnsureUtc(nowUtc);

        return new Session
        {
            Id = id,
            CandidateId = candidateId,
            ExamId = examId,
            Status = SessionStatus.Active,
            CreatedAt = nowUtc,
            ExpiresAt = nowUtc + maxDuration,
            TokenHash = tokenHash,
            ClientUserAgent = clientUserAgent,
            ClientAppVersion = clientAppVersion,
        };
    }

    /// <summary>Makes this session the result of an idempotent create that may be replayed until <paramref name="replayUntilUtc"/>.</summary>
    public void EnableIdempotentReplay(byte[] idempotencyKeyHash, byte[] requestFingerprint, byte[] derivationSalt, DateTime replayUntilUtc)
    {
        EnsureLength(idempotencyKeyHash, HashLength, nameof(idempotencyKeyHash));
        EnsureLength(requestFingerprint, HashLength, nameof(requestFingerprint));
        EnsureLength(derivationSalt, DerivationSaltLength, nameof(derivationSalt));
        EnsureUtc(replayUntilUtc);
        if (IdempotencyKeyHash is not null) throw new InvalidOperationException("Idempotency is already configured");
        IdempotencyKeyHash = idempotencyKeyHash;
        RequestFingerprint = requestFingerprint;
        TokenDerivationSalt = derivationSalt;
        IdempotencyExpiresAt = replayUntilUtc;
    }

    /// <summary>A retried create may return this session (and its token) only while it is active and inside the window.</summary>
    public bool CanReplayCreate(DateTime nowUtc) =>
        Status == SessionStatus.Active
        && nowUtc < ExpiresAt
        && TokenDerivationSalt is not null
        && IdempotencyExpiresAt is { } until
        && nowUtc < until;

    /// <summary>
    /// Erases the derivation salt once a replay is no longer allowed (window passed or session ended), so
    /// even someone holding both the database and the server secret cannot recreate the token afterwards.
    /// Returns true if something changed.
    /// </summary>
    public bool ForgetTokenDerivationIfNotReplayable(DateTime nowUtc)
    {
        if (TokenDerivationSalt is null || CanReplayCreate(nowUtc)) return false;
        TokenDerivationSalt = null;
        return true;
    }

    /// <summary>Ends the session. Returns false (and changes nothing) if it had already ended.</summary>
    public bool End(SessionEndReason reason, DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        if (Status == SessionStatus.Ended) return false;
        Status = SessionStatus.Ended;
        // An expired session ended at its expiry time, not when someone noticed.
        EndedAt = reason == SessionEndReason.Expired ? ExpiresAt : nowUtc;
        EndReason = reason;
        TokenDerivationSalt = null; // an ended session is never replayed
        return true;
    }

    /// <summary>Ends the session if its maximum duration has passed. Returns true if it changed.</summary>
    public bool ExpireIfDue(DateTime nowUtc) =>
        Status == SessionStatus.Active && nowUtc >= ExpiresAt && End(SessionEndReason.Expired, nowUtc);

    /// <summary>
    /// A token keeps working while the session is active and for a limited time after it ended
    /// (to read results and finish uploads); after that it is rejected.
    /// </summary>
    public bool IsTokenUsableAt(DateTime nowUtc, TimeSpan ttlAfterEnd)
    {
        var endedAt = Status == SessionStatus.Ended ? EndedAt : (nowUtc >= ExpiresAt ? ExpiresAt : null);
        return endedAt is null || nowUtc < endedAt.Value + ttlAfterEnd;
    }

    private static void EnsureLength(byte[] value, int length, string name)
    {
        if (value is null || value.Length != length)
            throw new ArgumentException($"{name} must be {length} bytes", name);
    }

    private static void EnsureUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamps must be UTC", nameof(value));
    }
}
