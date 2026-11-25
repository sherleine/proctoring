using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proctoring.Application.Errors;
using Proctoring.Application.Sessions;
using Proctoring.Application.Validation;
using Proctoring.Contracts.Events;
using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;

namespace Proctoring.Application.Events;

public enum UploadOutcome
{
    /// <summary>New event stored and queued (202).</summary>
    Accepted,
    /// <summary>Same eventId with identical metadata and audio was already received (200).</summary>
    Duplicate,
    /// <summary>New event stored (RECEIVED) but the queue refused it; it will be re-published (503).</summary>
    StoredQueueUnavailable,
}

public sealed record UploadResult(UploadOutcome Outcome, AudioEvent Event);

/// <summary>Use cases for audio events: authorize, upload, read.</summary>
public sealed class AudioEventService(
    SessionService sessions,
    IAudioEventRepository events,
    IAudioStore store,
    EventPublisher publisher,
    IContractValidator contracts,
    TimeProvider clock,
    IOptions<AudioEventOptions> eventOptions,
    IOptions<SessionOptions> sessionOptions,
    IOptions<RecordingPolicyOptions> policy,
    ILogger<AudioEventService> logger)
{
    public const string MetadataSchema = "audio-event-metadata.v1.json";
    public const double EnrolmentMaxSeconds = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AudioEventOptions _options = eventOptions.Value;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Largest audio part accepted, in bytes (from the session's recording policy).</summary>
    public long MaxAudioBytes => policy.Value.MaxUploadBytes;

    /// <summary>
    /// Authenticates the token for the session and checks that it still accepts uploads. Called before the
    /// body is read, so nothing is stored for unauthorized callers.
    /// </summary>
    public async Task<Session> AuthorizeUploadAsync(string sessionId, string token, CancellationToken ct)
    {
        var session = await sessions.AuthorizeAsync(sessionId, token, ct);
        EnsureAcceptingUploads(session);
        return session;
    }

    /// <summary>
    /// Validates and stores one event. Order (see docs/architecture.md "Upload and recovery"):
    /// validate staged audio + metadata -> insert DB row (RECEIVED) -> move file into place -> publish.
    /// A database failure therefore never leaves a permanent file; a file failure removes the row again.
    /// </summary>
    public async Task<UploadResult> UploadAsync(Session session, JsonNode? metadataNode, StagedAudio audio, CancellationToken ct)
    {
        EnsureAcceptingUploads(session);

        // 1. Metadata must match the contract schema.
        var schemaErrors = contracts.Validate(MetadataSchema, metadataNode);
        if (schemaErrors.Count > 0) throw new RequestValidationException(schemaErrors);
        var metadata = metadataNode.Deserialize<AudioEventMetadata>(Json)!;

        // 2. The audio must be a real 16 kHz / mono / 16-bit PCM WAV (header checked, not the file name).
        WavInfo wav;
        await using (var read = audio.OpenRead())
            wav = await WavValidator.ReadAsync(read, audio.SizeBytes, ct);
        WavValidator.RequireFormat(wav, AudioEventOptions.RequiredSampleRate, AudioEventOptions.RequiredChannels, AudioEventOptions.RequiredBitsPerSample);

        // 3. Metadata and audio must describe the same clip, inside this session.
        CrossCheck(session, metadata, wav, audio);

        var metadataJson = Canonical(metadataNode!);
        var metadataSha = Sha256Hex(metadataJson);

        // 4. Duplicate eventId in this session?
        var existing = await events.FindAsync(session.Id, metadata.EventId, ct);
        if (existing is not null) return await HandleDuplicateAsync(existing, metadataSha, audio, ct);

        // 5. New event: database row first, then the file.
        var storageKey = store.NewStorageKey(session.Id);
        var audioEvent = AudioEvent.Receive(
            session.Id, metadata.EventId, session.CandidateId, session.ExamId, metadata.SequenceNumber,
            metadata.StartedAt.UtcDateTime, metadata.EndedAt.UtcDateTime, metadata.SpeechStartedAt.UtcDateTime,
            Math.Round(wav.DurationSeconds, 3), wav.SampleRate, wav.Channels, audio.SizeBytes, audio.Sha256,
            metadataSha, metadataJson, storageKey, Now);
        events.Add(audioEvent);
        try
        {
            await events.SaveChangesAsync(ct);
        }
        catch (DuplicateEventException)
        {
            // A concurrent upload of the same eventId won the race.
            events.DetachAll();
            var winner = await events.FindAsync(session.Id, metadata.EventId, ct)
                         ?? throw new InvalidOperationException("Duplicate event reported but not found");
            return await HandleDuplicateAsync(winner, metadataSha, audio, ct);
        }

        try
        {
            await store.CommitAsync(audio, storageKey, ct);
        }
        catch (Exception ex)
        {
            // Compensate so no row points at a file that was never stored. If this also fails, maintenance
            // marks the row FAILED (AUDIO_FILE_MISSING) after EVENT_MISSING_AUDIO_AFTER_MINUTES.
            logger.LogError(ex, "Storing audio for event {EventId} failed; removing the event record", audioEvent.EventId);
            try
            {
                events.Remove(audioEvent);
                await events.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                logger.LogError(cleanup, "Could not remove event {EventId} after a storage failure", audioEvent.EventId);
            }
            throw;
        }

        logger.LogInformation("Event {EventId} received for session {SessionId}: {DurationSeconds:0.00} s, {SizeBytes} bytes",
            audioEvent.EventId, session.Id, audioEvent.DurationSeconds, audioEvent.SizeBytes);
        return await PublishNewAsync(audioEvent, ct);
    }

    public async Task<AudioEvent> GetAsync(string sessionId, string token, Guid eventId, CancellationToken ct)
    {
        var session = await sessions.AuthorizeAsync(sessionId, token, ct);
        return await events.FindAsync(session.Id, eventId, ct) ?? throw new EventNotFoundException();
    }

    public async Task<IReadOnlyList<AudioEvent>> ListAsync(string sessionId, string token, CancellationToken ct)
    {
        var session = await sessions.AuthorizeAsync(sessionId, token, ct);
        return await events.ListAsync(session.Id, _options.ListLimit, ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<UploadResult> PublishNewAsync(AudioEvent audioEvent, CancellationToken ct)
    {
        try
        {
            await publisher.PublishAsync(audioEvent, ct);
        }
        catch (QueueUnavailableException ex)
        {
            logger.LogWarning("Queue unavailable for event {EventId}; it stays RECEIVED and will be re-published: {Reason}",
                audioEvent.EventId, ex.Message);
            var stored = await events.FindAsync(audioEvent.SessionId, audioEvent.EventId, ct) ?? audioEvent;
            return new UploadResult(UploadOutcome.StoredQueueUnavailable, stored);
        }
        var current = await events.FindAsync(audioEvent.SessionId, audioEvent.EventId, ct) ?? audioEvent;
        return new UploadResult(UploadOutcome.Accepted, current);
    }

    private async Task<UploadResult> HandleDuplicateAsync(AudioEvent existing, string metadataSha, StagedAudio audio, CancellationToken ct)
    {
        if (!existing.IsSameUpload(metadataSha, audio.Sha256)) throw new EventConflictException();

        // Repair: the earlier request stored the row but crashed before the file was moved into place.
        // Only for rows old enough to be abandoned - a younger row belongs to a request that is still
        // moving its file right now, and must not be raced.
        var abandoned = existing.ReceivedAt < Now.AddSeconds(-_options.RepairAfterSeconds);
        if (existing.Status == AudioEventStatus.Received && abandoned
            && (existing.StorageKey is null || !await store.ExistsAsync(existing.StorageKey, ct)))
        {
            var key = existing.StorageKey ?? store.NewStorageKey(existing.SessionId);
            try
            {
                await store.CommitAsync(audio, key, ct);
                logger.LogInformation("Event {EventId}: audio re-stored from a retried upload", existing.EventId);
            }
            catch (IOException)
            {
                // Another retry stored the same (checksum-verified) file first: nothing left to repair.
                if (!await store.ExistsAsync(key, ct)) throw;
            }
            if (existing.StorageKey is null)
            {
                existing.AttachStorage(key, Now);
                await events.SaveChangesAsync(ct);
            }
        }

        if (existing.Status == AudioEventStatus.Received)
        {
            try
            {
                await publisher.PublishAsync(existing, ct);
            }
            catch (QueueUnavailableException)
            {
                // Still RECEIVED; maintenance re-publishes it. The retry is answered with the current state.
            }
            existing = await events.FindAsync(existing.SessionId, existing.EventId, ct) ?? existing;
        }
        return new UploadResult(UploadOutcome.Duplicate, existing);
    }

    private void EnsureAcceptingUploads(Session session)
    {
        if (session.Status == SessionStatus.Active) return;
        var grace = TimeSpan.FromSeconds(sessionOptions.Value.UploadGraceSeconds);
        if (session.EndedAt is { } ended && Now <= ended + grace) return; // finishing uploads after the end
        throw new SessionNotAcceptingUploadsException(
            session.EndReason == SessionEndReason.Expired ? "The session has expired" : "The session has ended");
    }

    private void CrossCheck(Session session, AudioEventMetadata m, WavInfo wav, StagedAudio audio)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string key, string message) => errors[key] = [message];
        var tolerance = _options.DurationToleranceSeconds;
        // The voice sample (enrolment, contract 1.1) is longer than a speech event: up to the schema's 30 s.
        var maxSeconds = m.EventType == "enrolment" ? EnrolmentMaxSeconds : policy.Value.MaxEventSeconds + 1;

        if (wav.DurationSeconds < _options.MinDurationSeconds)
            Add("audio", $"The audio is too short ({wav.DurationSeconds:0.000} s; minimum {_options.MinDurationSeconds} s)");
        if (wav.DurationSeconds > maxSeconds)
            Add("audio", $"The audio is too long ({wav.DurationSeconds:0.00} s; maximum {maxSeconds} s)");
        if (Math.Abs(wav.DurationSeconds - m.DurationSeconds) > tolerance)
            Add("durationSeconds", $"durationSeconds ({m.DurationSeconds}) differs from the audio ({wav.DurationSeconds:0.000} s) by more than {tolerance} s");
        if (m.Audio.SizeBytes != audio.SizeBytes)
            Add("audio.sizeBytes", $"audio.sizeBytes ({m.Audio.SizeBytes}) does not match the uploaded file ({audio.SizeBytes})");
        if (!string.Equals(m.Audio.Sha256, audio.Sha256, StringComparison.Ordinal))
            Add("audio.sha256", "audio.sha256 does not match the uploaded file");
        if (m.EndedAt <= m.StartedAt)
            Add("endedAt", "endedAt must be after startedAt");
        else if (Math.Abs((m.EndedAt - m.StartedAt).TotalSeconds - m.DurationSeconds) > tolerance)
            Add("endedAt", "endedAt - startedAt must equal durationSeconds");
        if (m.SpeechStartedAt < m.StartedAt || m.SpeechStartedAt > m.EndedAt)
            Add("speechStartedAt", "speechStartedAt must lie between startedAt and endedAt");
        if (m.StartedAt.UtcDateTime < session.CreatedAt - _options.ClockSkew)
            Add("startedAt", "The event starts before the session started");
        if (m.EndedAt.UtcDateTime > (session.EndedAt ?? Now) + _options.ClockSkew)
            Add("endedAt", "The event ends after the session ended (or in the future)");
        if (m.Video.Uploaded)
            Add("video.uploaded", "Video upload is not supported in this version; send audio only");

        if (errors.Count > 0) throw new RequestValidationException(errors);
    }

    /// <summary>Canonical JSON (object keys sorted, no whitespace) so equal metadata hashes equally regardless of key order.</summary>
    internal static string Canonical(JsonNode node)
    {
        static JsonNode? Sort(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
            null => null,
            _ => n.DeepClone(),
        };
        return Sort(node)!.ToJsonString();
    }

    internal static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
