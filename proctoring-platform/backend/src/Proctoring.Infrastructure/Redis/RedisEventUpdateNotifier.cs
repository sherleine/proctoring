using Microsoft.Extensions.Logging;
using Proctoring.Application.Events;
using Proctoring.Infrastructure.Notifications;
using StackExchange.Redis;

namespace Proctoring.Infrastructure.Redis;

/// <summary>
/// Multi-instance wake-up for SSE streams: a change is signalled locally and published (session id only) on
/// REDIS_UPDATES_CHANNEL so streams held by other backend instances wake up too. Pub/sub is fire-and-forget and
/// not durable; that is fine because the data itself is read from SQLite and streams also poll.
/// </summary>
public sealed class RedisEventUpdateNotifier(RedisConnection redis, ILogger<RedisEventUpdateNotifier> logger) : IEventUpdateNotifier
{
    private readonly InProcessEventUpdateNotifier _local = new();
    private readonly RedisChannel _channel = RedisChannel.Literal(redis.Settings.UpdatesChannel);
    private int _subscribed; // 0 = not yet, 1 = subscribing/subscribed

    public void Notify(string sessionId)
    {
        _local.Notify(sessionId);
        _ = PublishAsync(sessionId);
    }

    public async Task WaitAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        EnsureSubscribed();
        await _local.WaitAsync(sessionId, timeout, ct);
    }

    private async Task PublishAsync(string sessionId)
    {
        try
        {
            var subscriber = await redis.GetSubscriberAsync();
            await subscriber.PublishAsync(_channel, sessionId, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Update signal not published ({Error}); other instances pick it up by polling", ex.GetType().Name);
        }
    }

    private void EnsureSubscribed()
    {
        if (Interlocked.CompareExchange(ref _subscribed, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var subscriber = await redis.GetSubscriberAsync();
                // The multiplexer re-establishes this subscription after reconnects.
                await subscriber.SubscribeAsync(_channel, (_, value) => { if (value.HasValue) _local.Notify(value!); });
            }
            catch (Exception ex)
            {
                logger.LogDebug("Update channel not subscribed yet ({Error}); will retry", ex.GetType().Name);
                Volatile.Write(ref _subscribed, 0);
            }
        });
    }
}
