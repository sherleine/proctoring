using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Proctoring.Application.Events;
using Proctoring.Application.Validation;

namespace Proctoring.Infrastructure.Redis;

/// <summary>
/// Turns a results-stream entry into a <see cref="ProcessingMessage"/> and applies it. A fresh DI scope (and
/// therefore a fresh DbContext / database connection) is used per entry and disposed right after.
/// </summary>
public sealed class ResultsMessageHandler(IServiceScopeFactory scopes, IContractValidator contracts)
{
    public const string EnvelopeSchema = "stream-envelope.v1.json";

    public async Task<ProcessingOutcome> HandleAsync(IReadOnlyDictionary<string, string> fields, int deliveryCount, CancellationToken ct)
    {
        var envelope = new JsonObject();
        foreach (var (k, v) in fields) envelope[k] = v;
        var errors = contracts.Validate(EnvelopeSchema, envelope);
        if (errors.Count > 0)
            return ProcessingOutcome.DeadLetter("envelope violates " + EnvelopeSchema + ": " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));

        var message = new ProcessingMessage(fields["messageType"], fields["schemaVersion"], fields["messageId"],
            fields["sessionId"], fields["eventId"], fields["payload"]);
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ResultIngestionService>().ApplyAsync(message, deliveryCount, ct);
    }
}

/// <summary>
/// Runs the backend's consumer of REDIS_RESULTS_STREAM (group REDIS_BACKEND_GROUP). Until a processing
/// service exists the stream is simply empty and this waits; it never produces or invents results.
/// Redis outages are logged and retried with back-off; the API keeps serving meanwhile.
/// </summary>
public sealed class ResultsConsumerWorker(
    RedisConnection redis,
    ResultsMessageHandler handler,
    TimeProvider clock,
    ILogger<ResultsConsumerWorker> logger) : BackgroundService
{
    private readonly RedisSettings _settings = redis.Settings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = new StreamConsumerOptions(_settings.ResultsStream, _settings.BackendGroup, _settings.ResultsDlqStream,
            RedisStreamConsumer.NewConsumerName(), _settings.MaxDeliveries, _settings.ClaimIdleMs, _settings.ClaimIntervalMs,
            _settings.ReadBatch, _settings.StreamMaxLength);
        var consumer = new RedisStreamConsumer(redis, options, handler.HandleAsync, clock, logger);
        logger.LogInformation("Results consumer {Consumer} started on {Stream} (group {Group})", options.ConsumerName, options.Stream, options.Group);

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await consumer.RunOnceAsync(stoppingToken);
                if (failures > 0) logger.LogInformation("Results consumer recovered");
                failures = 0;
                if (handled == 0) await Task.Delay(_settings.PollMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, Math.Min(failures, 6))));
                if (failures == 1 || failures % 20 == 0)
                    logger.LogWarning("Results consumer cannot read {Stream} ({Error}); retrying in {Delay}", options.Stream, ex.GetType().Name, delay);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
