using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Proctoring.Application.Events;
using Proctoring.Application.Sessions;
using Proctoring.Application.Validation;
using Proctoring.Infrastructure.Notifications;
using Proctoring.Infrastructure.Persistence;
using Proctoring.Infrastructure.Queue;
using Proctoring.Infrastructure.Redis;
using Proctoring.Infrastructure.Security;
using Proctoring.Infrastructure.Storage;
using Proctoring.Infrastructure.Validation;

namespace Proctoring.Infrastructure;

/// <summary>Resolved infrastructure locations and secrets (paths are absolute).</summary>
public sealed record InfrastructureSettings(
    string DatabasePath,
    string SchemaDirectory,
    SessionTokenSecret TokenSecret,
    string AudioStorePath,
    string QueueProvider,
    string QueueDirectory,
    RedisSettings? Redis = null);

public static class DependencyInjection
{
    /// <summary>QUEUE_PROVIDER values. "local" is an alias of "local-directory".</summary>
    public const string LocalDirectoryQueue = "local-directory";
    public const string LocalQueueAlias = "local";
    public const string RedisStreamsQueue = "redis-streams";

    public static string NormalizeQueueProvider(string value) => value == LocalQueueAlias ? LocalDirectoryQueue : value;

    /// <summary>Registers persistence, security, contract validation, audio storage and the event queue.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, InfrastructureSettings settings)
    {
        services.AddSingleton(settings.TokenSecret);
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
        services.AddDbContext<ProctoringDbContext>(o => o.UseSqlite($"Data Source={settings.DatabasePath}"));
        services.AddScoped<ISessionRepository, EfSessionRepository>();
        services.AddScoped<IAudioEventRepository, EfAudioEventRepository>();
        services.AddScoped<IEventUpdateRepository, EfEventUpdateRepository>();
        services.AddScoped<IObservationRepository, EfObservationRepository>();
        services.AddSingleton<ISessionTokenService, SessionTokenService>();
        services.AddSingleton<ISessionIdGenerator, SessionIdGenerator>();
        services.AddSingleton<IContractValidator>(_ => new JsonSchemaContractValidator(settings.SchemaDirectory));

        services.AddSingleton<IAudioStore>(_ => new LocalFileAudioStore(settings.AudioStorePath));
        var health = services.AddHealthChecks().AddDbContextCheck<ProctoringDbContext>("database", tags: ["ready"]);

        // No silent fallback: the configured provider is used, or startup fails.
        switch (NormalizeQueueProvider(settings.QueueProvider))
        {
            case LocalDirectoryQueue:
                services.AddSingleton<IAudioEventQueue>(new LocalDirectoryAudioEventQueue(settings.QueueDirectory));
                services.AddSingleton<IEventUpdateNotifier, InProcessEventUpdateNotifier>();
                break;
            case RedisStreamsQueue:
                var redis = settings.Redis ?? throw new InvalidOperationException("QUEUE_PROVIDER=redis-streams requires Redis settings");
                services.AddSingleton(redis);
                services.AddSingleton<RedisConnection>();
                services.AddSingleton<IAudioEventQueue, RedisStreamsAudioEventQueue>();
                services.AddSingleton<IEventUpdateNotifier, RedisEventUpdateNotifier>();
                services.AddSingleton<ResultsMessageHandler>();
                if (redis.ResultsConsumerEnabled) services.AddHostedService<ResultsConsumerWorker>();
                health.AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
                break;
            default:
                throw new InvalidOperationException($"Unknown QUEUE_PROVIDER '{settings.QueueProvider}'");
        }

        services.AddScoped<SessionService>();
        services.AddScoped<EventPublisher>();
        services.AddScoped<AudioEventService>();
        services.AddScoped<EventMaintenance>();
        services.AddScoped<ResultIngestionService>();
        services.AddScoped<ObservationQueryService>();
        return services;
    }
}
