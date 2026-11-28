using System.Text.Json.Nodes;

namespace Proctoring.Contracts.Events;

// HTTP DTOs for docs/contracts/schemas/event-status.v1.json and audio-event-metadata.v1.json.

/// <summary>event-status.v1.json. No storage paths or internal references are ever included.</summary>
public sealed record EventStatusResponse
{
    public required string EventId { get; init; }
    public required string SessionId { get; init; }
    public required int SequenceNumber { get; init; }

    /// <summary>RECEIVED | QUEUED | PROCESSING | COMPLETED | FAILED | DEAD_LETTERED</summary>
    public required string Status { get; init; }

    public required DateTime ReceivedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public required DateTime StartedAt { get; init; }

    /// <summary>Contract 1.3 addition (optional).</summary>
    public required DateTime EndedAt { get; init; }

    /// <summary>Measured from the stored WAV.</summary>
    public required double DurationSeconds { get; init; }

    public required int Attempts { get; init; }
    public required EventErrorDto? Error { get; init; }

    /// <summary>analysis-result.v1.json once processing completed; null until then.</summary>
    public required JsonNode? Result { get; init; }

    /// <summary>Contract 1.3 addition (optional): facts about the stored audio.</summary>
    public required EventAudioDto Audio { get; init; }
}

public sealed record EventErrorDto(string Code, string Message);

public sealed record EventAudioDto(string Format, string Codec, int SampleRate, int Channels, long SizeBytes, string Sha256, bool Retained);

public sealed record EventListResponse(IReadOnlyList<EventStatusResponse> Items);

/// <summary>The fields of audio-event-metadata.v1.json the backend uses (the full document is validated by schema).</summary>
public sealed record AudioEventMetadata
{
    public required Guid EventId { get; init; }
    public required int SequenceNumber { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required DateTimeOffset SpeechStartedAt { get; init; }
    public required double DurationSeconds { get; init; }
    public required MetadataAudio Audio { get; init; }
    public required MetadataVideo Video { get; init; }
}

public sealed record MetadataAudio(string Format, string Codec, int SampleRate, int Channels, long SizeBytes, string Sha256);

public sealed record MetadataVideo(bool RecordedLocally, bool Uploaded);

public static class EventStatusValues
{
    public const string Received = "RECEIVED";
    public const string Queued = "QUEUED";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string DeadLettered = "DEAD_LETTERED";
}
