using System.Text.Json;
using Proctoring.Application.Events;

namespace Proctoring.Infrastructure.Queue;

/// <summary>
/// DEVELOPMENT PLACEHOLDER - not the Redis Streams integration.
///
/// Writes each "audio.event.submitted" message (the exact stream-entry fields of
/// docs/contracts/redis-streams.v1.md) as one JSON file in QUEUE_LOCAL_DIRECTORY, named after the event.
/// Nothing consumes these files: events reach QUEUED and stay there. The point is that no accepted event is
/// lost or silently dropped, and that the message contract is exercised end to end before Redis exists.
///
/// Durable: temp file + atomic rename. Idempotent: re-publishing an event overwrites its own file.
/// Fails loudly (QueueUnavailableException) when the directory cannot be written, which drives the same
/// RECEIVED + re-publish path a Redis outage will use.
/// </summary>
public sealed class LocalDirectoryAudioEventQueue : IAudioEventQueue
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _directory;

    public LocalDirectoryAudioEventQueue(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public string Name => "local-directory (placeholder, no consumer)";

    public string QueueDirectory => _directory;

    public async Task PublishAsync(AudioEventSubmittedMessage message, CancellationToken ct)
    {
        var eventId = Guid.Parse(message.EventId); // validated GUID -> safe file name
        var target = Path.Combine(_directory, $"{message.SessionId}_{eventId:N}.json");
        var temp = target + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(message.ToFields(), Json), ct);
            await Storage.FileMoves.MoveAsync(temp, target, overwrite: true, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort */ }
            throw new QueueUnavailableException($"Local queue directory is not writable: {ex.GetType().Name}", ex);
        }
    }
}
