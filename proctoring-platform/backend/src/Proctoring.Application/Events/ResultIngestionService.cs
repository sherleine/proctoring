using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Proctoring.Application.Validation;
using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;

namespace Proctoring.Application.Events;

/// <summary>A processing-service message (docs/contracts/redis-streams.v1.md), transport-independent.</summary>
public sealed record ProcessingMessage(
    string MessageType,
    string SchemaVersion,
    string MessageId,
    string SessionId,
    string EventId,
    string PayloadJson);

public enum ProcessingDisposition
{
    /// <summary>Durably applied: acknowledge.</summary>
    Applied,
    /// <summary>Nothing to do (duplicate, or the event is already final): acknowledge.</summary>
    Ignored,
    /// <summary>Temporary problem (e.g. database unavailable): do NOT acknowledge; it will be redelivered.</summary>
    Retry,
    /// <summary>Can never succeed (invalid, unknown event, unsupported version): move to the dead-letter stream.</summary>
    DeadLetter,
}

public sealed record ProcessingOutcome(ProcessingDisposition Disposition, string? Reason = null)
{
    public static readonly ProcessingOutcome Applied = new(ProcessingDisposition.Applied);
    public static ProcessingOutcome Ignored(string reason) => new(ProcessingDisposition.Ignored, reason);
    public static ProcessingOutcome Retry(string reason) => new(ProcessingDisposition.Retry, reason);
    public static ProcessingOutcome DeadLetter(string reason) => new(ProcessingDisposition.DeadLetter, reason);
}

/// <summary>
/// The one place where processing results change event state. Transport-agnostic: the Redis results consumer
/// calls it today; an HTTP results endpoint could call it later. Every message is validated against the
/// versioned contract schemas, applied with the domain's idempotent rules, and saved in one transaction
/// together with its change-log entry. The caller acknowledges the message only after this returns
/// Applied/Ignored, so a crash before the save simply causes a redelivery.
/// </summary>
public sealed class ResultIngestionService(
    IAudioEventRepository events,
    IContractValidator contracts,
    TimeProvider clock,
    ILogger<ResultIngestionService> logger,
    IObservationRepository? observations = null,
    IVoiceprintRepository? voiceprints = null)
{
    public const string StartedType = "audio.analysis.started";
    public const string CompletedType = "audio.analysis.completed";
    public const string FailedType = "audio.analysis.failed";
    public const string SupportedVersion = "1";
    public const string DeadLetteredCode = "DEAD_LETTERED";

    public async Task<ProcessingOutcome> ApplyAsync(ProcessingMessage message, int deliveryCount, CancellationToken ct)
    {
        if (message.SchemaVersion != SupportedVersion)
            return ProcessingOutcome.DeadLetter($"Unsupported schemaVersion '{message.SchemaVersion}'");
        if (!Guid.TryParse(message.EventId, out var eventId))
            return ProcessingOutcome.DeadLetter("eventId is not a UUID");

        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(message.PayloadJson);
        }
        catch (JsonException)
        {
            return ProcessingOutcome.DeadLetter("payload is not valid JSON");
        }

        var schema = message.MessageType switch
        {
            StartedType => "analysis-started.v1.json",
            CompletedType or FailedType => "analysis-result.v1.json",
            _ => null,
        };
        if (schema is null) return ProcessingOutcome.DeadLetter($"Unknown messageType '{message.MessageType}'");
        var errors = contracts.Validate(schema, payload);
        if (errors.Count > 0)
            return ProcessingOutcome.DeadLetter($"payload violates {schema}: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
        if ((string?)payload!["sessionId"] != message.SessionId || (string?)payload["eventId"] != message.EventId)
            return ProcessingOutcome.DeadLetter("payload sessionId/eventId do not match the envelope");

        var expectedStatus = message.MessageType switch { CompletedType => "completed", FailedType => "failed", _ => null };
        if (expectedStatus is not null && (string?)payload["status"] != expectedStatus)
            return ProcessingOutcome.DeadLetter($"{message.MessageType} must carry status '{expectedStatus}'");

        // A concurrent writer (another instance, maintenance) makes the save fail; re-read and decide again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(message, eventId, payload!, deliveryCount, ct);
            }
            catch (EventConcurrencyException) when (attempt < 3)
            {
                events.DetachAll();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Applying {MessageType} for event {EventId} failed ({Error}); it will be redelivered",
                    message.MessageType, eventId, ex.GetType().Name);
                events.DetachAll();
                return ProcessingOutcome.Retry(ex.GetType().Name);
            }
        }
    }

    private async Task<ProcessingOutcome> ApplyOnceAsync(ProcessingMessage message, Guid eventId, JsonNode payload, int deliveryCount, CancellationToken ct)
    {
        {
            var audioEvent = await events.FindAsync(message.SessionId, eventId, ct);
            if (audioEvent is null) return ProcessingOutcome.DeadLetter("Unknown event (sessionId/eventId)");

            var now = clock.GetUtcNow().UtcDateTime;
            // Contract 1.1: the enrolment embedding is biometric data. It is moved to the session's voiceprint and
            // removed from the result document, which is what browsers can read.
            var (resultJson, embeddingJson) = message.MessageType == CompletedType ? SplitEmbedding(payload) : (null, null);
            var changed = message.MessageType switch
            {
                StartedType => audioEvent.ApplyAnalysisStarted((int?)payload["deliveryCount"] ?? deliveryCount, now),
                CompletedType => audioEvent.ApplyAnalysisCompleted(resultJson!, deliveryCount, now),
                _ => ApplyFailure(audioEvent, payload, deliveryCount, now),
            };
            if (changed && message.MessageType == CompletedType)
                await StoreDerivedAsync(audioEvent, payload, embeddingJson, now, ct);
            if (!changed)
            {
                logger.LogInformation("{MessageType} for event {EventId} ignored (event is {Status})", message.MessageType, eventId, audioEvent.Status);
                return ProcessingOutcome.Ignored($"event is {audioEvent.Status}");
            }

            await events.SaveChangesAsync(ct); // event + change-log entry in one transaction
            logger.LogInformation("{MessageType} applied to event {EventId}: now {Status}", message.MessageType, eventId, audioEvent.Status);
            return ProcessingOutcome.Applied;
        }
    }

    /// <summary>The result document without the embedding (replaced by embeddingStored=true), and the embedding itself.</summary>
    private static (string ResultJson, string? EmbeddingJson) SplitEmbedding(JsonNode payload)
    {
        var copy = payload.DeepClone();
        if (copy["enrolment"] is JsonObject enrolment && enrolment["embedding"] is JsonArray embedding)
        {
            var embeddingJson = embedding.ToJsonString();
            enrolment["embedding"] = null;
            enrolment["embeddingStored"] = true;
            return (copy.ToJsonString(), embeddingJson);
        }
        return (copy.ToJsonString(), null);
    }

    /// <summary>Voiceprint (enrolment events) and observation rows, saved in the same transaction as the event.</summary>
    private async Task StoreDerivedAsync(AudioEvent audioEvent, JsonNode payload, string? embeddingJson, DateTime now, CancellationToken ct)
    {
        if (voiceprints is not null && payload["enrolment"] is JsonObject enrolment && IsEnrolmentEvent(audioEvent))
        {
            var status = (string?)enrolment["status"] ?? "rejected";
            var reason = (string?)enrolment["reason"];
            var speech = (double?)enrolment["speechSeconds"] ?? 0;
            var model = (string?)enrolment["embeddingModel"] ?? "unknown";
            var existing = await voiceprints.FindAsync(audioEvent.SessionId, ct);
            if (existing is null)
                voiceprints.Add(SessionVoiceprint.Create(audioEvent.SessionId, audioEvent.EventId, status, reason, speech, embeddingJson, model, now));
            else
                existing.Apply(audioEvent.EventId, status, reason, speech, embeddingJson, model, now);
            logger.LogInformation("Voice sample of session {SessionId} processed: {Status}", audioEvent.SessionId, status);
        }

        if (observations is null || payload["observations"] is not JsonArray items) return;
        foreach (var item in items.OfType<JsonObject>())
        {
            // The schema guarantees the shape; the identifiers must also belong to THIS event.
            if ((string?)item["sessionId"] != audioEvent.SessionId || (string?)item["eventId"] != audioEvent.EventId.ToString())
            {
                logger.LogWarning("Observation for another event ignored (event {EventId})", audioEvent.EventId);
                continue;
            }
            var id = Guid.Parse((string)item["observationId"]!);
            if (await observations.ExistsAsync(id, ct)) continue;
            observations.Add(AudioObservation.Create(
                id, audioEvent, (string)item["observationType"]!,
                (double)item["startSeconds"]!, Math.Max((double)item["startSeconds"]!, (double)item["endSeconds"]!),
                (double)item["confidence"]!, (string)item["confidenceKind"]!,
                item["supportingEvidence"]!.ToJsonString(), (string)item["modelName"]!, (string)item["modelVersion"]!,
                DateTime.Parse((string)item["createdAt"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                now, (string?)item["category"]));
        }
    }

    private static bool IsEnrolmentEvent(AudioEvent e)
    {
        try
        {
            return (string?)JsonNode.Parse(e.MetadataJson)?["eventType"] == "enrolment";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ApplyFailure(AudioEvent e, JsonNode payload, int deliveryCount, DateTime now)
    {
        var error = payload["error"];
        var code = (string?)error?["code"] ?? "PROCESSING_FAILED";
        var text = (string?)error?["message"] ?? "Processing failed";
        var attempts = (int?)error?["deliveryCount"] ?? deliveryCount;
        // Truncate: the message comes from another service and is shown to clients.
        if (text.Length > 500) text = text[..500];
        return e.ApplyAnalysisFailed(code, text, deadLettered: code == DeadLetteredCode, attempts, now);
    }
}
