namespace Proctoring.Domain.Events;

/// <summary>Processing lifecycle from docs/contracts (event-status.v1.json).</summary>
public enum AudioEventStatus
{
    /// <summary>Stored by the backend; not yet handed to the queue.</summary>
    Received,
    /// <summary>Published to the processing queue.</summary>
    Queued,
    /// <summary>An AI worker is analysing it (set by the results consumer, next phase).</summary>
    Processing,
    Completed,
    /// <summary>Processing failed (or the audio could not be kept); see ErrorCode.</summary>
    Failed,
    /// <summary>Gave up after the maximum number of deliveries.</summary>
    DeadLettered,
}

/// <summary>
/// One uploaded speech event (audio clip + metadata). Identified by (SessionId, EventId): event IDs are
/// chosen by the client and are unique within their session only, so one session can never collide with,
/// or learn about, another session's events. The audio itself lives in the audio store; only an opaque
/// storage key is kept here.
/// </summary>
public sealed class AudioEvent
{
    public string SessionId { get; private set; } = default!;
    public Guid EventId { get; private set; }

    /// <summary>Copied from the authenticated session, never taken from the upload.</summary>
    public string CandidateId { get; private set; } = default!;
    public string ExamId { get; private set; } = default!;

    public int SequenceNumber { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime EndedAt { get; private set; }
    public DateTime SpeechStartedAt { get; private set; }

    /// <summary>Measured from the WAV data by the server (not the client's claim).</summary>
    public double DurationSeconds { get; private set; }

    public string AudioFormat { get; private set; } = "wav";
    public string AudioCodec { get; private set; } = "pcm_s16le";
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public long SizeBytes { get; private set; }
    public string AudioSha256 { get; private set; } = default!;

    /// <summary>SHA-256 of the canonical metadata JSON; with AudioSha256 it identifies "the same upload".</summary>
    public string MetadataSha256 { get; private set; } = default!;

    /// <summary>The validated metadata document, forwarded unchanged to the processing queue.</summary>
    public string MetadataJson { get; private set; } = default!;

    /// <summary>Opaque key in the audio store; null once the audio has been deleted (retention). Never sent to clients.</summary>
    public string? StorageKey { get; private set; }
    public DateTime? AudioDeletedAt { get; private set; }

    public DateTime ReceivedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? QueuedAt { get; private set; }
    public AudioEventStatus Status { get; private set; }

    /// <summary>AI deliveries so far (maintained by the results consumer, next phase).</summary>
    public int Attempts { get; private set; }

    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>analysis-result.v1.json document once processing completed (next phase).</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Short lease so that only one publisher enqueues the event at a time.</summary>
    public DateTime? PublishLeaseUntil { get; private set; }

    private AudioEvent() { } // EF Core

    public static AudioEvent Receive(
        string sessionId,
        Guid eventId,
        string candidateId,
        string examId,
        int sequenceNumber,
        DateTime startedAt,
        DateTime endedAt,
        DateTime speechStartedAt,
        double durationSeconds,
        int sampleRate,
        int channels,
        long sizeBytes,
        string audioSha256,
        string metadataSha256,
        string metadataJson,
        string storageKey,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session id is required", nameof(sessionId));
        if (eventId == Guid.Empty) throw new ArgumentException("Event id is required", nameof(eventId));
        if (endedAt <= startedAt) throw new ArgumentException("An event must end after it starts", nameof(endedAt));
        if (durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (sizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (string.IsNullOrWhiteSpace(storageKey)) throw new ArgumentException("Storage key is required", nameof(storageKey));
        foreach (var t in new[] { startedAt, endedAt, speechStartedAt, nowUtc })
            if (t.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamps must be UTC");

        return new AudioEvent
        {
            SessionId = sessionId,
            EventId = eventId,
            CandidateId = candidateId,
            ExamId = examId,
            SequenceNumber = sequenceNumber,
            StartedAt = startedAt,
            EndedAt = endedAt,
            SpeechStartedAt = speechStartedAt,
            DurationSeconds = durationSeconds,
            SampleRate = sampleRate,
            Channels = channels,
            SizeBytes = sizeBytes,
            AudioSha256 = audioSha256,
            MetadataSha256 = metadataSha256,
            MetadataJson = metadataJson,
            StorageKey = storageKey,
            ReceivedAt = nowUtc,
            UpdatedAt = nowUtc,
            Status = AudioEventStatus.Received,
        };
    }

    /// <summary>True when a new upload is the same event (same metadata and same audio bytes).</summary>
    public bool IsSameUpload(string metadataSha256, string audioSha256) =>
        string.Equals(MetadataSha256, metadataSha256, StringComparison.Ordinal)
        && string.Equals(AudioSha256, audioSha256, StringComparison.Ordinal);

    // ------------------------------------------------------------------ lifecycle

    private static readonly Dictionary<AudioEventStatus, AudioEventStatus[]> Allowed = new()
    {
        [AudioEventStatus.Received] = [AudioEventStatus.Queued, AudioEventStatus.Failed],
        [AudioEventStatus.Queued] = [AudioEventStatus.Processing, AudioEventStatus.Completed, AudioEventStatus.Failed, AudioEventStatus.DeadLettered],
        [AudioEventStatus.Processing] = [AudioEventStatus.Completed, AudioEventStatus.Failed, AudioEventStatus.DeadLettered, AudioEventStatus.Queued],
        [AudioEventStatus.Failed] = [AudioEventStatus.Queued, AudioEventStatus.Processing, AudioEventStatus.Completed, AudioEventStatus.DeadLettered],
        [AudioEventStatus.Completed] = [],
        [AudioEventStatus.DeadLettered] = [],
    };

    public static bool CanTransition(AudioEventStatus from, AudioEventStatus to) => Allowed[from].Contains(to);

    private void MoveTo(AudioEventStatus next, DateTime nowUtc)
    {
        if (!CanTransition(Status, next))
            throw new InvalidOperationException($"Event status cannot change from {Status} to {next}");
        Status = next;
        UpdatedAt = nowUtc;
    }

    public void MarkQueued(DateTime nowUtc)
    {
        MoveTo(AudioEventStatus.Queued, nowUtc);
        QueuedAt = nowUtc;
        PublishLeaseUntil = null;
        ErrorCode = ErrorMessage = null;
    }

    /// <summary>Technical failure (e.g. stored audio missing). Not a judgement about the candidate.</summary>
    public void MarkFailed(string code, string message, DateTime nowUtc)
    {
        MoveTo(AudioEventStatus.Failed, nowUtc);
        ErrorCode = code;
        ErrorMessage = message;
        PublishLeaseUntil = null;
    }

    // ------------------------------------------------------------------ analysis results (from the processing service)
    // These are idempotent: a result message can be delivered more than once (at-least-once streams).
    // COMPLETED and DEAD_LETTERED are final - later or duplicate messages never overwrite them.

    /// <summary>True when no further analysis message may change this event.</summary>
    public bool IsFinal => Status is AudioEventStatus.Completed or AudioEventStatus.DeadLettered;

    /// <summary>
    /// A message published before the backend recorded QUEUED (crash between XADD and the status update) can be
    /// answered while the event is still RECEIVED: it was demonstrably queued, so record that first.
    /// </summary>
    private void EnsureQueued(DateTime nowUtc)
    {
        if (Status == AudioEventStatus.Received) MarkQueued(nowUtc);
    }

    /// <summary>A worker started analysing the event. Returns false when the message changes nothing.</summary>
    public bool ApplyAnalysisStarted(int deliveryCount, DateTime nowUtc)
    {
        if (IsFinal) return false;
        // A late "started" of an attempt that already failed must not reopen the event.
        if (Status == AudioEventStatus.Failed && deliveryCount <= Attempts) return false;
        EnsureQueued(nowUtc);
        var changed = false;
        if (Status != AudioEventStatus.Processing)
        {
            MoveTo(AudioEventStatus.Processing, nowUtc);
            changed = true;
        }
        if (deliveryCount > Attempts)
        {
            Attempts = deliveryCount;
            UpdatedAt = nowUtc;
            changed = true;
        }
        return changed;
    }

    /// <summary>Stores the analysis result. A COMPLETED event keeps its first result (duplicates are ignored).</summary>
    public bool ApplyAnalysisCompleted(string resultJson, int deliveryCount, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) throw new ArgumentException("Result is required", nameof(resultJson));
        if (IsFinal) return false;
        EnsureQueued(nowUtc);
        MoveTo(AudioEventStatus.Completed, nowUtc);
        ResultJson = resultJson;
        Attempts = Math.Max(Attempts, deliveryCount);
        ErrorCode = ErrorMessage = null;
        return true;
    }

    /// <summary>
    /// Processing failed. <paramref name="deadLettered"/> means the processor gave up (DEAD_LETTERED, final);
    /// otherwise the event is FAILED and may still be retried by the processor.
    /// </summary>
    public bool ApplyAnalysisFailed(string code, string message, bool deadLettered, int deliveryCount, DateTime nowUtc)
    {
        if (IsFinal) return false;
        var target = deadLettered ? AudioEventStatus.DeadLettered : AudioEventStatus.Failed;
        // Exact duplicate of the failure already recorded.
        if (Status == target && ErrorCode == code && ErrorMessage == message && deliveryCount <= Attempts) return false;
        EnsureQueued(nowUtc);
        if (Status != target) MoveTo(target, nowUtc);
        else UpdatedAt = nowUtc;
        ErrorCode = code;
        ErrorMessage = message;
        Attempts = Math.Max(Attempts, deliveryCount);
        return true;
    }

    /// <summary>Records that the stored audio was removed (retention); the event record stays.</summary>
    public void AudioRemoved(DateTime nowUtc)
    {
        StorageKey = null;
        AudioDeletedAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    /// <summary>Points the event at a (re)stored audio file - used to repair a crash between database and file writes.</summary>
    public void AttachStorage(string storageKey, DateTime nowUtc)
    {
        if (Status != AudioEventStatus.Received) throw new InvalidOperationException("Audio can only be re-attached before queueing");
        StorageKey = storageKey;
        UpdatedAt = nowUtc;
    }
}
