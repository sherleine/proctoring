using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proctoring.Application.Validation;
using Proctoring.Domain.Events;

namespace Proctoring.Application.Events;

public enum PublishOutcome
{
    /// <summary>Handed to the queue and marked QUEUED.</summary>
    Queued,
    /// <summary>Not RECEIVED any more, or another publisher holds the lease.</summary>
    NotNeeded,
    /// <summary>Stored audio missing; left for maintenance.</summary>
    AudioMissing,
}

/// <summary>
/// Moves a RECEIVED event to the queue exactly once per lease:
///   1. atomically claim a short publish lease (only one caller wins),
///   2. publish the "audio.event.submitted" message,
///   3. mark the event QUEUED (only if still RECEIVED).
/// A crash between 2 and 3 leaves the event RECEIVED; after the lease expires it is published again
/// (at-least-once). Consumers de-duplicate by eventId, as the stream contract requires.
/// </summary>
public sealed class EventPublisher(
    IAudioEventRepository events,
    IAudioStore store,
    IAudioEventQueue queue,
    IContractValidator contracts,
    TimeProvider clock,
    IOptions<AudioEventOptions> options,
    ILogger<EventPublisher> logger)
{
    public const string MessageSchema = "audio-event-message.v1.json";
    private readonly AudioEventOptions _options = options.Value;

    public async Task<PublishOutcome> PublishAsync(AudioEvent audioEvent, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await events.TryClaimForPublishAsync(audioEvent.SessionId, audioEvent.EventId, now,
                now.AddSeconds(_options.PublishLeaseSeconds), ct))
            return PublishOutcome.NotNeeded;

        try
        {
            if (audioEvent.StorageKey is null || !await store.ExistsAsync(audioEvent.StorageKey, ct))
            {
                await events.ReleasePublishClaimAsync(audioEvent.SessionId, audioEvent.EventId, ct);
                return PublishOutcome.AudioMissing;
            }

            var message = BuildMessage(audioEvent, now);
            await queue.PublishAsync(message, ct); // throws QueueUnavailableException
        }
        catch
        {
            await events.ReleasePublishClaimAsync(audioEvent.SessionId, audioEvent.EventId, CancellationToken.None);
            throw;
        }

        await events.MarkQueuedAsync(audioEvent.SessionId, audioEvent.EventId, clock.GetUtcNow().UtcDateTime, ct);
        logger.LogInformation("Event {EventId} of session {SessionId} queued via {Queue}", audioEvent.EventId, audioEvent.SessionId, queue.Name);
        return PublishOutcome.Queued;
    }

    /// <summary>Builds the stream message and checks it against audio-event-message.v1.json before sending.</summary>
    public AudioEventSubmittedMessage BuildMessage(AudioEvent e, DateTime nowUtc)
    {
        var payload = new JsonObject
        {
            ["sessionId"] = e.SessionId,
            ["candidateId"] = e.CandidateId,
            ["examId"] = e.ExamId,
            ["event"] = JsonNode.Parse(e.MetadataJson),
            ["audioRef"] = new JsonObject
            {
                ["storage"] = "file",
                ["uri"] = store.GetProcessingUri(e.StorageKey!).AbsoluteUri,
                ["sha256"] = e.AudioSha256,
                ["sizeBytes"] = e.SizeBytes,
                ["contentType"] = "audio/wav",
                ["key"] = e.StorageKey,
            },
        };
        var errors = contracts.Validate(MessageSchema, payload);
        if (errors.Count > 0)
            throw new InvalidOperationException("Outgoing message violates " + MessageSchema + ": " +
                                                string.Join("; ", errors.Select(x => $"{x.Key}: {string.Join(",", x.Value)}")));

        return new AudioEventSubmittedMessage(
            MessageId: Guid.NewGuid().ToString(),
            CorrelationId: e.EventId.ToString(),
            SessionId: e.SessionId,
            EventId: e.EventId.ToString(),
            ProducedAt: nowUtc,
            PayloadJson: payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }
}
