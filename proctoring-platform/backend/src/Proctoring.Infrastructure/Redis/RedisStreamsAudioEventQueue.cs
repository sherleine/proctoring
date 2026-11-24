using Microsoft.Extensions.Logging;
using Proctoring.Application.Events;
using StackExchange.Redis;

namespace Proctoring.Infrastructure.Redis;

/// <summary>
/// Publishes "audio.event.submitted" with XADD to REDIS_EVENTS_STREAM. The entry holds exactly the envelope
/// fields of docs/contracts/redis-streams.v1.md; the payload references the audio (storage key + sha256), it never
/// contains audio bytes. Any Redis failure surfaces as <see cref="QueueUnavailableException"/>: the event stays
/// RECEIVED in SQLite (the outbox) and maintenance publishes it again. There is no fallback to another queue.
/// </summary>
public sealed class RedisStreamsAudioEventQueue(RedisConnection redis, ILogger<RedisStreamsAudioEventQueue> logger) : IAudioEventQueue
{
    private readonly RedisSettings _settings = redis.Settings;
    private volatile bool _aiGroupEnsured;

    public string Name => "redis-streams";

    public async Task PublishAsync(AudioEventSubmittedMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var db = await redis.GetDatabaseAsync();
            if (_settings.CreateAiGroup && !_aiGroupEnsured)
            {
                await RedisConnection.EnsureGroupAsync(db, _settings.EventsStream, _settings.AiGroup);
                _aiGroupEnsured = true;
            }

            var fields = message.ToFields().Select(f => new NameValueEntry(f.Key, f.Value)).ToArray();
            int? maxLength = _settings.StreamMaxLength > 0 ? _settings.StreamMaxLength : null;
            var id = await db.StreamAddAsync(_settings.EventsStream, fields, maxLength: maxLength, useApproximateMaxLength: true);
            logger.LogDebug("Event {EventId} added to {Stream} as {EntryId}", message.EventId, _settings.EventsStream, (string?)id);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Only the exception type: Redis messages can contain endpoint details.
            throw new QueueUnavailableException($"Redis is unavailable ({ex.GetType().Name})", ex);
        }
    }
}
