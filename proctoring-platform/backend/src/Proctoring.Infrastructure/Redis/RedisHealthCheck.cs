using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Proctoring.Infrastructure.Redis;

/// <summary>
/// Readiness: Redis answers PING within REDIS_OPERATION_TIMEOUT_MS. Only registered when QUEUE_PROVIDER is
/// redis-streams. The result never contains the connection string, user or password, nor the exception.
/// </summary>
public sealed class RedisHealthCheck(RedisConnection redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var db = await redis.GetDatabaseAsync().WaitAsync(TimeSpan.FromMilliseconds(redis.Settings.OperationTimeoutMs), ct);
            var latency = await db.PingAsync().WaitAsync(TimeSpan.FromMilliseconds(redis.Settings.OperationTimeoutMs), ct);
            return HealthCheckResult.Healthy($"Redis reachable ({latency.TotalMilliseconds:0.#} ms)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable");
        }
    }
}
