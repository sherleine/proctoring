namespace Proctoring.Domain.Sessions;

/// <summary>
/// The candidate's voice reference for ONE session, built by the AI service from the enrolment sample.
/// Biometric data: stored only here (never in the result returned to browsers), sent only to the AI service
/// inside the internal Redis message of the same session, and deleted by retention.
/// One row per session: a newer enrolment replaces the older one; embeddings are never shared between sessions.
/// </summary>
public sealed class SessionVoiceprint
{
    public string SessionId { get; private set; } = default!;
    public Guid EnrolmentEventId { get; private set; }

    /// <summary>accepted | uncertain | rejected. Only an accepted voiceprint is used for comparisons.</summary>
    public string Status { get; private set; } = default!;
    public string? Reason { get; private set; }
    public double SpeechSeconds { get; private set; }

    /// <summary>JSON array of floats; null when the sample was rejected.</summary>
    public string? EmbeddingJson { get; private set; }
    public string Model { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public bool IsUsable => Status == "accepted" && EmbeddingJson is not null;

    private SessionVoiceprint() { } // EF Core

    public static SessionVoiceprint Create(string sessionId, Guid enrolmentEventId, string status, string? reason,
        double speechSeconds, string? embeddingJson, string model, DateTime nowUtc)
    {
        var v = new SessionVoiceprint { SessionId = sessionId, CreatedAt = nowUtc };
        v.Apply(enrolmentEventId, status, reason, speechSeconds, embeddingJson, model, nowUtc);
        return v;
    }

    /// <summary>A newer enrolment of the same session replaces the reference.</summary>
    public void Apply(Guid enrolmentEventId, string status, string? reason, double speechSeconds, string? embeddingJson,
        string model, DateTime nowUtc)
    {
        if (status is not ("accepted" or "uncertain" or "rejected")) throw new ArgumentException("Unknown enrolment status", nameof(status));
        EnrolmentEventId = enrolmentEventId;
        Status = status;
        Reason = reason is { Length: > 500 } ? reason[..500] : reason;
        SpeechSeconds = speechSeconds;
        EmbeddingJson = status == "rejected" ? null : embeddingJson;
        Model = model;
        UpdatedAt = nowUtc;
    }
}
