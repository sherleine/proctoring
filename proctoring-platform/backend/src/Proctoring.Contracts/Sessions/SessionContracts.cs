using System.Text.Json.Serialization;

namespace Proctoring.Contracts.Sessions;

// HTTP DTOs for docs/contracts/schemas/session-create.request.v1.json and session.v1.json.
// Property names serialize to camelCase (ASP.NET Core default) and must match the schemas exactly.

public sealed record CreateSessionRequest(string CandidateId, string ExamId, ClientInfo? Client);

public sealed record ClientInfo(string? UserAgent, string? AppVersion);

public sealed record RecordingPolicyDto(
    double PreEventSeconds,
    double SilenceSeconds,
    double MaxEventSeconds,
    double SplitOverlapSeconds,
    bool UploadVideo,
    long MaxUploadBytes);

public sealed record SessionResponse
{
    public required string SessionId { get; init; }

    /// <summary>Only present in the response that created (or idempotently re-issued) the session.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionToken { get; init; }

    public required string CandidateId { get; init; }
    public required string ExamId { get; init; }

    /// <summary>"active" | "ended"</summary>
    public required string Status { get; init; }

    public required DateTime CreatedAt { get; init; }

    /// <summary>Always serialized (null while active); required by session.v1.json.</summary>
    public required DateTime? EndedAt { get; init; }

    /// <summary>When the session ends automatically (contract addition 1.1, optional field).</summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>"candidate" | "expired" | null (contract addition 1.1, optional field).</summary>
    public required string? EndReason { get; init; }

    public required RecordingPolicyDto RecordingPolicy { get; init; }
}

public static class SessionStatusValues
{
    public const string Active = "active";
    public const string Ended = "ended";
}

public static class SessionEndReasonValues
{
    public const string Candidate = "candidate";
    public const string Expired = "expired";
}
