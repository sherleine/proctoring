using System.ComponentModel.DataAnnotations;

namespace Proctoring.Application.Sessions;

/// <summary>Session lifetime settings (environment: SESSION_*).</summary>
public sealed class SessionOptions
{
    /// <summary>SESSION_MAX_DURATION_HOURS: sessions end automatically after this long.</summary>
    [Range(0.01, 48)]
    public double MaxDurationHours { get; set; } = 6;

    /// <summary>SESSION_UPLOAD_GRACE_SECONDS: uploads still accepted this long after the session ended (Phase 3).</summary>
    [Range(0, 3600)]
    public int UploadGraceSeconds { get; set; } = 120;

    /// <summary>SESSION_TOKEN_TTL_AFTER_END_HOURS: a token stops working this long after its session ended.</summary>
    [Range(0.01, 720)]
    public double TokenTtlAfterEndHours { get; set; } = 24;

    /// <summary>
    /// SESSION_IDEMPOTENCY_WINDOW_MINUTES: how long a retried create (same Idempotency-Key and body) returns
    /// the same session and token. Short by design: during the window the key can recover the token.
    /// </summary>
    [Range(1, 1440)]
    public int IdempotencyWindowMinutes { get; set; } = 60;

    public TimeSpan MaxDuration => TimeSpan.FromHours(MaxDurationHours);
    public TimeSpan TokenTtlAfterEnd => TimeSpan.FromHours(TokenTtlAfterEndHours);
    public TimeSpan IdempotencyWindow => TimeSpan.FromMinutes(IdempotencyWindowMinutes);
}

/// <summary>
/// SESSION_TOKEN_SECRET: 32+ random bytes (base64). Used only to derive tokens of idempotent creates
/// (HMAC-SHA256). Required in Production; Development/Testing use a random per-process secret if unset.
/// </summary>
public sealed class SessionTokenSecret
{
    public const int MinimumBytes = 32;

    public SessionTokenSecret(byte[] key, bool isEphemeral)
    {
        if (key.Length < MinimumBytes) throw new ArgumentException($"Secret must be at least {MinimumBytes} bytes", nameof(key));
        Key = key;
        IsEphemeral = isEphemeral;
    }

    public byte[] Key { get; }

    /// <summary>True when generated at startup (replays do not survive a restart).</summary>
    public bool IsEphemeral { get; }

    public override string ToString() => "[redacted]"; // never print the key
}

/// <summary>
/// Recording parameters sent to the browser in every session response (environment: RECORDING_*).
/// Ranges mirror recordingPolicy in docs/contracts/schemas/session.v1.json.
/// </summary>
public sealed class RecordingPolicyOptions
{
    [Range(0, 5)] public double PreEventSeconds { get; set; } = 2.5;
    [Range(0.3, 5)] public double SilenceSeconds { get; set; } = 1.5;
    [Range(2, 30)] public double MaxEventSeconds { get; set; } = 10;
    [Range(0, 3)] public double SplitOverlapSeconds { get; set; } = 1;
    public bool UploadVideo { get; set; }
    [Range(1024, 50_000_000)] public long MaxUploadBytes { get; set; } = 2_000_000;
}
