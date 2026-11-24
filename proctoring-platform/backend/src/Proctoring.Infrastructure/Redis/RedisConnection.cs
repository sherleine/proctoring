using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Proctoring.Infrastructure.Redis;

/// <summary>
/// The process-wide Redis multiplexer (StackExchange.Redis is designed to share one). Created lazily so the
/// application starts even when Redis is down; the multiplexer then keeps reconnecting in the background.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _multiplexer;

    public RedisConnection(RedisSettings settings, ILogger<RedisConnection> logger)
    {
        Settings = settings;
        _multiplexer = new Lazy<Task<IConnectionMultiplexer>>(async () =>
        {
            var name = $"proctoring-backend-{Environment.MachineName}-{Environment.ProcessId}";
            var mux = await ConnectionMultiplexer.ConnectAsync(settings.ToConfigurationOptions(name));
            mux.ConnectionFailed += (_, e) =>
                logger.LogWarning("Redis connection lost ({FailureType}); reconnecting", e.FailureType);
            mux.ConnectionRestored += (_, e) => logger.LogInformation("Redis connection restored");
            logger.LogInformation("Redis client created for {Redis}; connected={Connected}", settings, mux.IsConnected);
            return mux;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RedisSettings Settings { get; }

    public async Task<IDatabase> GetDatabaseAsync() => (await _multiplexer.Value).GetDatabase();

    public async Task<ISubscriber> GetSubscriberAsync() => (await _multiplexer.Value).GetSubscriber();

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer.IsValueCreated && _multiplexer.Value.IsCompletedSuccessfully)
            await _multiplexer.Value.Result.DisposeAsync();
    }

    /// <summary>Redis client exceptions that mean "Redis is unavailable right now".</summary>
    public static bool IsUnavailable(Exception ex) =>
        ex is RedisConnectionException or RedisTimeoutException or TimeoutException
        || (ex is RedisServerException server && server.Message.StartsWith("LOADING", StringComparison.Ordinal));

    /// <summary>Creates a consumer group starting at the first entry; "already exists" is success.</summary>
    public static async Task EnsureGroupAsync(IDatabase db, string stream, string group)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(stream, group, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
            // Created earlier (by us, another instance or the processing service).
        }
    }
}
