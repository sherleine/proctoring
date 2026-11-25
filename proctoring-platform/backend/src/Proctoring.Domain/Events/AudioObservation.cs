namespace Proctoring.Domain.Events;

/// <summary>
/// A potential observation produced by the AI service for one audio event (contract 1.1, analysis-result
/// "observations"). It is evidence for a human reviewer, never a determination of misconduct. Stored as its own
/// row so it can be queried per session; the full result document stays on the event.
/// </summary>
public sealed class AudioObservation
{
    public const int MaxEvidenceChars = 16_000;

    /// <summary>Deterministic id chosen by the AI service: a redelivered result never creates a second row.</summary>
    public Guid Id { get; private set; }
    public string SessionId { get; private set; } = default!;
    public Guid EventId { get; private set; }
    public string ObservationType { get; private set; } = default!;

    /// <summary>informational (SPEECH_DETECTED / NO_SPEECH_DETECTED) or review (a potential observation).</summary>
    public string Category { get; private set; } = "review";

    public static readonly IReadOnlySet<string> InformationalTypes = new HashSet<string>(StringComparer.Ordinal)
        { "SPEECH_DETECTED", "NO_SPEECH_DETECTED" };

    /// <summary>Category when the producer did not send one (results before the field existed).</summary>
    public static string InferCategory(string observationType) =>
        InformationalTypes.Contains(observationType) ? "informational" : "review";

    /// <summary>Offsets inside the event's audio clip.</summary>
    public double StartSeconds { get; private set; }
    public double EndSeconds { get; private set; }

    /// <summary>Absolute time = event StartedAt + StartSeconds (client clock, as checked at upload).</summary>
    public DateTime OccurredAt { get; private set; }

    /// <summary>0..1. See ConfidenceKind: "model-score" is NOT a probability.</summary>
    public double Confidence { get; private set; }
    public string ConfidenceKind { get; private set; } = default!;
    public string SupportingEvidenceJson { get; private set; } = default!;
    public string ModelName { get; private set; } = default!;
    public string ModelVersion { get; private set; } = default!;

    /// <summary>When the AI created it.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>When the backend stored it.</summary>
    public DateTime StoredAt { get; private set; }

    private AudioObservation() { } // EF Core

    public static AudioObservation Create(Guid id, AudioEvent audioEvent, string observationType, double startSeconds,
        double endSeconds, double confidence, string confidenceKind, string supportingEvidenceJson, string modelName,
        string modelVersion, DateTime createdAt, DateTime storedAt, string? category = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Observation id is required", nameof(id));
        if (string.IsNullOrWhiteSpace(observationType)) throw new ArgumentException("Type is required", nameof(observationType));
        if (startSeconds < 0 || endSeconds < startSeconds) throw new ArgumentException("Invalid time range");
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (supportingEvidenceJson.Length > MaxEvidenceChars) supportingEvidenceJson = "{\"truncated\":true}";
        return new AudioObservation
        {
            Id = id,
            SessionId = audioEvent.SessionId,
            EventId = audioEvent.EventId,
            ObservationType = observationType,
            Category = category is "informational" or "review" ? category : InferCategory(observationType),
            StartSeconds = startSeconds,
            EndSeconds = endSeconds,
            OccurredAt = audioEvent.StartedAt.AddSeconds(startSeconds),
            Confidence = confidence,
            ConfidenceKind = confidenceKind,
            SupportingEvidenceJson = supportingEvidenceJson,
            ModelName = modelName,
            ModelVersion = modelVersion,
            CreatedAt = createdAt.ToUniversalTime(),
            StoredAt = storedAt,
        };
    }
}
