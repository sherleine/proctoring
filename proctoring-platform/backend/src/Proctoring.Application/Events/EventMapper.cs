using System.Text.Json.Nodes;
using Proctoring.Contracts.Events;
using Proctoring.Domain.Events;

namespace Proctoring.Application.Events;

public static class EventMapper
{
    public static string ToContract(AudioEventStatus status) => status switch
    {
        AudioEventStatus.Received => EventStatusValues.Received,
        AudioEventStatus.Queued => EventStatusValues.Queued,
        AudioEventStatus.Processing => EventStatusValues.Processing,
        AudioEventStatus.Completed => EventStatusValues.Completed,
        AudioEventStatus.Failed => EventStatusValues.Failed,
        AudioEventStatus.DeadLettered => EventStatusValues.DeadLettered,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Client view of an event. Deliberately leaves out StorageKey and any other internal reference.</summary>
    public static EventStatusResponse ToResponse(AudioEvent e) => new()
    {
        EventId = e.EventId.ToString(),
        SessionId = e.SessionId,
        SequenceNumber = e.SequenceNumber,
        Status = ToContract(e.Status),
        ReceivedAt = e.ReceivedAt,
        UpdatedAt = e.UpdatedAt,
        StartedAt = e.StartedAt,
        EndedAt = e.EndedAt,
        DurationSeconds = e.DurationSeconds,
        Attempts = e.Attempts,
        Error = e.ErrorCode is null ? null : new EventErrorDto(e.ErrorCode, e.ErrorMessage ?? e.ErrorCode),
        Result = e.ResultJson is null ? null : JsonNode.Parse(e.ResultJson),
        Audio = new EventAudioDto(e.AudioFormat, e.AudioCodec, e.SampleRate, e.Channels, e.SizeBytes, e.AudioSha256, e.StorageKey is not null),
    };
}
