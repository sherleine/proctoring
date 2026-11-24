using Proctoring.Domain.Events;

namespace Proctoring.Application.Events;

/// <summary>
/// Where audio files live. The local-directory implementation can be replaced by S3 or another object
/// store without touching the upload logic: callers only ever see opaque storage keys.
///
/// Write protocol (see docs/architecture.md, "Upload and recovery"):
///   1. <see cref="StageAsync"/> streams the upload into a private staging area (never referenced by the DB).
///   2. The database row is written with a key from <see cref="NewStorageKey"/>.
///   3. <see cref="CommitAsync"/> moves the staged file to that key.
/// Disposing an uncommitted <see cref="StagedAudio"/> deletes the staged file.
/// </summary>
public interface IAudioStore
{
    /// <summary>Streams <paramref name="content"/> into staging, computing size and SHA-256 as it goes.
    /// Stops and throws <see cref="Errors.AudioTooLargeException"/> as soon as more than <paramref name="maxBytes"/> arrive.</summary>
    Task<StagedAudio> StageAsync(Stream content, long maxBytes, CancellationToken ct);

    /// <summary>A new server-generated key for a session's audio (client input never becomes part of a path).</summary>
    string NewStorageKey(string sessionId);

    /// <summary>Moves the staged file to <paramref name="storageKey"/> (atomic rename on the same volume).</summary>
    Task CommitAsync(StagedAudio staged, string storageKey, CancellationToken ct);

    Task<bool> ExistsAsync(string storageKey, CancellationToken ct);

    Task DeleteAsync(string storageKey, CancellationToken ct);

    /// <summary>Internal reference for the processing service (e.g. file:///data/audio/...). Never sent to browsers.</summary>
    Uri GetProcessingUri(string storageKey);

    /// <summary>Deletes staged files left behind by crashes or aborted uploads. Returns how many were removed.</summary>
    Task<int> DeleteStaleStagingAsync(TimeSpan olderThan, CancellationToken ct);
}

/// <summary>An upload stored in staging. Deleted on dispose unless committed.</summary>
public abstract class StagedAudio : IAsyncDisposable
{
    public required long SizeBytes { get; init; }

    /// <summary>Lower-case hex SHA-256 of the exact bytes received.</summary>
    public required string Sha256 { get; init; }

    public bool Committed { get; protected set; }

    public abstract Stream OpenRead();

    public abstract ValueTask DisposeAsync();
}

/// <summary>
/// Hands accepted events to the processing pipeline. The current implementation is a local placeholder;
/// the Redis Streams implementation (next phase) plugs in here without changing the upload API.
/// </summary>
public interface IAudioEventQueue
{
    /// <summary>Short name for logs/health (e.g. "local-directory", "redis-streams").</summary>
    string Name { get; }

    /// <summary>Publishes the message. Throws <see cref="QueueUnavailableException"/> when it cannot be accepted.
    /// Must be safe to call twice for the same event (at-least-once delivery).</summary>
    Task PublishAsync(AudioEventSubmittedMessage message, CancellationToken ct);
}

/// <summary>An "audio.event.submitted" stream entry (docs/contracts/redis-streams.v1.md).</summary>
public sealed record AudioEventSubmittedMessage(
    string MessageId,
    string CorrelationId,
    string SessionId,
    string EventId,
    DateTime ProducedAt,
    string PayloadJson)
{
    public const string Type = "audio.event.submitted";
    public const string Version = "1";

    /// <summary>The flat field/value pairs of the stream entry.</summary>
    public IReadOnlyDictionary<string, string> ToFields() => new Dictionary<string, string>
    {
        ["messageType"] = Type,
        ["schemaVersion"] = Version,
        ["messageId"] = MessageId,
        ["correlationId"] = CorrelationId,
        ["sessionId"] = SessionId,
        ["eventId"] = EventId,
        ["producedAt"] = ProducedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["payload"] = PayloadJson,
    };
}

public sealed class QueueUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAudioEventRepository
{
    Task<AudioEvent?> FindAsync(string sessionId, Guid eventId, CancellationToken ct);

    /// <summary>Newest first (highest sequence number, then latest receipt).</summary>
    Task<IReadOnlyList<AudioEvent>> ListAsync(string sessionId, int max, CancellationToken ct);

    void Add(AudioEvent audioEvent);
    void Remove(AudioEvent audioEvent);

    /// <summary>Throws <see cref="DuplicateEventException"/> when (sessionId, eventId) already exists.</summary>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Atomically takes the publish lease of a RECEIVED event (only one publisher wins).</summary>
    Task<bool> TryClaimForPublishAsync(string sessionId, Guid eventId, DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct);

    /// <summary>RECEIVED -> QUEUED, only if still RECEIVED. Returns false if someone else changed it.</summary>
    Task<bool> MarkQueuedAsync(string sessionId, Guid eventId, DateTime nowUtc, CancellationToken ct);

    Task ReleasePublishClaimAsync(string sessionId, Guid eventId, CancellationToken ct);

    /// <summary>RECEIVED events received before <paramref name="receivedBeforeUtc"/> whose lease is free.</summary>
    Task<IReadOnlyList<AudioEvent>> FindUnpublishedAsync(DateTime receivedBeforeUtc, DateTime nowUtc, int max, CancellationToken ct);

    /// <summary>Events that still have stored audio and were received before the cut-off (retention).</summary>
    Task<IReadOnlyList<AudioEvent>> FindWithAudioReceivedBeforeAsync(DateTime receivedBeforeUtc, int max, CancellationToken ct);

    /// <summary>Forget tracked entities (after a failed save) so the unit of work can be reused.</summary>
    void DetachAll();
}

public sealed class DuplicateEventException(Exception inner) : Exception("The event already exists in this session", inner);

/// <summary>The event changed after it was read (another writer won). Re-read and decide again.</summary>
public sealed class EventConcurrencyException(Exception inner) : Exception("The event was changed concurrently", inner);
