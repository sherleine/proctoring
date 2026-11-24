using System.ComponentModel.DataAnnotations;
using StackExchange.Redis;

namespace Proctoring.Infrastructure.Redis;

/// <summary>
/// Redis Streams settings (environment: REDIS_*, RESULTS_*). Stream and group names are part of
/// docs/contracts/redis-streams.v1.md; change them only together with the processing service.
/// </summary>
public sealed class RedisSettings
{
    /// <summary>REDIS_CONNECTION: "host:port" (comma-separated for several endpoints). No credentials here.</summary>
    [Required] public string Connection { get; set; } = "localhost:6379";

    /// <summary>REDIS_USER: ACL user name (optional).</summary>
    public string? User { get; set; }

    /// <summary>REDIS_PASSWORD: never logged and never shown by health checks.</summary>
    public string? Password { get; set; }

    /// <summary>REDIS_SSL: use TLS.</summary>
    public bool Ssl { get; set; }

    /// <summary>REDIS_CONNECT_TIMEOUT_MS</summary>
    [Range(100, 60_000)] public int ConnectTimeoutMs { get; set; } = 5000;

    /// <summary>REDIS_OPERATION_TIMEOUT_MS: per-command timeout; an upload never waits longer than this for Redis.</summary>
    [Range(100, 60_000)] public int OperationTimeoutMs { get; set; } = 3000;

    /// <summary>REDIS_EVENTS_STREAM: backend -> processing service ("audio.event.submitted").</summary>
    [Required] public string EventsStream { get; set; } = "proctoring.audio.events.v1";

    /// <summary>REDIS_DLQ_STREAM: dead letters of the events stream (written by the processing service).</summary>
    [Required] public string EventsDlqStream { get; set; } = "proctoring.audio.events.dlq.v1";

    /// <summary>REDIS_RESULTS_STREAM: processing service -> backend (started / completed / failed).</summary>
    [Required] public string ResultsStream { get; set; } = "proctoring.audio.results.v1";

    /// <summary>REDIS_RESULTS_DLQ_STREAM: result messages the backend could not apply.</summary>
    [Required] public string ResultsDlqStream { get; set; } = "proctoring.audio.results.dlq.v1";

    /// <summary>REDIS_AI_GROUP: consumer group of the processing service on the events stream.</summary>
    [Required] public string AiGroup { get; set; } = "ai-audio-processors";

    /// <summary>REDIS_CREATE_AI_GROUP: create the processing group (from the first entry) when publishing, so
    /// events published before the processing service first starts are not skipped by it.</summary>
    public bool CreateAiGroup { get; set; } = true;

    /// <summary>REDIS_BACKEND_GROUP: consumer group of the backend on the results stream.</summary>
    [Required] public string BackendGroup { get; set; } = "backend-results";

    /// <summary>REDIS_STREAM_MAXLEN: approximate cap for each stream (0 = no trimming).</summary>
    [Range(0, int.MaxValue)] public int StreamMaxLength { get; set; } = 100_000;

    /// <summary>REDIS_UPDATES_CHANNEL: pub/sub channel that wakes SSE streams on other backend instances.</summary>
    [Required] public string UpdatesChannel { get; set; } = "proctoring.audio.updates.v1";

    /// <summary>RESULTS_CONSUMER_ENABLED: run the results consumer in this process.</summary>
    public bool ResultsConsumerEnabled { get; set; } = true;

    /// <summary>RESULTS_CLAIM_IDLE_MS: a pending result idle this long (its consumer crashed) is claimed by another.</summary>
    [Range(1000, 3_600_000)] public int ClaimIdleMs { get; set; } = 60_000;

    /// <summary>RESULTS_CLAIM_INTERVAL_MS: how often stale pending results are looked for.</summary>
    [Range(100, 3_600_000)] public int ClaimIntervalMs { get; set; } = 15_000;

    /// <summary>RESULTS_MAX_DELIVERIES: a result delivered more often than this goes to the dead-letter stream.</summary>
    [Range(1, 100)] public int MaxDeliveries { get; set; } = 3;

    /// <summary>RESULTS_READ_BATCH: entries read per XREADGROUP.</summary>
    [Range(1, 1000)] public int ReadBatch { get; set; } = 10;

    /// <summary>RESULTS_POLL_MS: pause between reads when the stream is empty.</summary>
    [Range(10, 60_000)] public int PollMs { get; set; } = 500;

    /// <summary>Client options. AbortOnConnectFail=false: the app starts while Redis is down and reconnects
    /// in the background; meanwhile commands fail fast and readiness reports unhealthy.</summary>
    public ConfigurationOptions ToConfigurationOptions(string clientName)
    {
        var options = ConfigurationOptions.Parse(Connection);
        options.User = string.IsNullOrEmpty(User) ? options.User : User;
        options.Password = string.IsNullOrEmpty(Password) ? options.Password : Password;
        options.Ssl = Ssl || options.Ssl;
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = ConnectTimeoutMs;
        options.SyncTimeout = OperationTimeoutMs;
        options.AsyncTimeout = OperationTimeoutMs;
        options.ConnectRetry = 3;
        options.ReconnectRetryPolicy = new ExponentialRetry(500, 10_000);
        options.KeepAlive = 30;
        // While disconnected, fail commands immediately instead of queueing them: an upload then answers 503 at
        // once (its event is already safe as RECEIVED in SQLite and is re-published by maintenance).
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ClientName = clientName;
        return options;
    }

    /// <summary>Safe for logs: endpoints and names only, never the password.</summary>
    public override string ToString() =>
        $"{Connection} (tls={Ssl}, auth={(string.IsNullOrEmpty(Password) ? "none" : string.IsNullOrEmpty(User) ? "default user" : "acl user")})";
}
