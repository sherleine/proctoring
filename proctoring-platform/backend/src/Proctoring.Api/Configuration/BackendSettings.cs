using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Proctoring.Application.Events;
using Proctoring.Application.Sessions;
using Proctoring.Infrastructure.Redis;
using SessionOptions = Proctoring.Application.Sessions.SessionOptions;

namespace Proctoring.Api.Configuration;

/// <summary>Infrastructure settings read from flat environment variables (see .env.example).</summary>
public sealed class BackendSettings
{
    /// <summary>DATABASE_PATH: SQLite file. Relative paths resolve against the working directory.</summary>
    [Required] public string DatabasePath { get; set; } = "data/db/proctoring.db";

    /// <summary>DATABASE_MIGRATE_ON_START: apply pending EF Core migrations at startup.</summary>
    public bool MigrateOnStart { get; set; } = true;

    /// <summary>CORS_ORIGINS: comma-separated browser origins allowed to call the API.</summary>
    public string[] CorsOrigins { get; set; } = [];

    /// <summary>CONTRACTS_SCHEMA_DIR: JSON Schemas (defaults to the copy shipped with the build).</summary>
    [Required] public string ContractsSchemaDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "contracts", "schemas");

    /// <summary>LOG_FORMAT: "json" (structured, default) or "text".</summary>
    [AllowedValues("json", "text")] public string LogFormat { get; set; } = "json";

    /// <summary>Maximum request body for session endpoints (bytes).</summary>
    public int MaxJsonBodyBytes { get; set; } = 16 * 1024;

    /// <summary>AUDIO_STORE_PATH: audio files (relative paths resolve against the API content root).</summary>
    [Required] public string AudioStorePath { get; set; } = "data/audio";

    /// <summary>QUEUE_PROVIDER: "redis-streams", or "local" / "local-directory" (development placeholder without consumer).</summary>
    [AllowedValues("local", "local-directory", "redis-streams")] public string QueueProvider { get; set; } = "local-directory";

    /// <summary>QUEUE_LOCAL_DIRECTORY: where the placeholder queue writes messages.</summary>
    [Required] public string QueueLocalDirectory { get; set; } = "data/queue";
}

/// <summary>
/// Maps the flat environment variable names used across all services (RECORDING_*, SESSION_*, ...)
/// onto typed options, and validates them at startup so a bad value fails fast with a clear message.
/// </summary>
public static class SettingsRegistration
{

    public static (BackendSettings Backend, SessionTokenSecret TokenSecret, RedisSettings? Redis) AddValidatedSettings(
        this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var errors = new List<string>();

        var backend = new BackendSettings();
        Bind(config, "DATABASE_PATH", v => backend.DatabasePath = v, errors);
        BindParsed<bool>(config, "DATABASE_MIGRATE_ON_START", bool.TryParse, v => backend.MigrateOnStart = v, errors);
        Bind(config, "CORS_ORIGINS", v => backend.CorsOrigins = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), errors);
        Bind(config, "CONTRACTS_SCHEMA_DIR", v => backend.ContractsSchemaDirectory = v, errors);
        Bind(config, "LOG_FORMAT", v => backend.LogFormat = v.ToLowerInvariant(), errors);
        Bind(config, "AUDIO_STORE_PATH", v => backend.AudioStorePath = v, errors);
        Bind(config, "QUEUE_PROVIDER", v => backend.QueueProvider = v.ToLowerInvariant(), errors);
        Bind(config, "QUEUE_LOCAL_DIRECTORY", v => backend.QueueLocalDirectory = v, errors);

        var events = new AudioEventOptions();
        BindParsed<double>(config, "AUDIO_MIN_DURATION_SECONDS", TryDouble, v => events.MinDurationSeconds = v, errors);
        BindParsed<double>(config, "AUDIO_DURATION_TOLERANCE_SECONDS", TryDouble, v => events.DurationToleranceSeconds = v, errors);
        BindParsed<double>(config, "AUDIO_RETENTION_HOURS", TryDouble, v => events.AudioRetentionHours = v, errors);
        BindParsed<int>(config, "EVENT_CLOCK_SKEW_SECONDS", int.TryParse, v => events.ClockSkewSeconds = v, errors);
        BindParsed<bool>(config, "EVENT_MAINTENANCE_ENABLED", bool.TryParse, v => events.MaintenanceEnabled = v, errors);
        BindParsed<int>(config, "EVENT_MAINTENANCE_INTERVAL_SECONDS", int.TryParse, v => events.MaintenanceIntervalSeconds = v, errors);
        BindParsed<int>(config, "EVENT_REPUBLISH_AFTER_SECONDS", int.TryParse, v => events.RepublishAfterSeconds = v, errors);
        BindParsed<int>(config, "EVENT_MISSING_AUDIO_AFTER_MINUTES", int.TryParse, v => events.MissingAudioAfterMinutes = v, errors);
        BindParsed<int>(config, "EVENT_STAGING_MAX_AGE_MINUTES", int.TryParse, v => events.StagingMaxAgeMinutes = v, errors);

        var live = new LiveUpdateOptions();
        BindParsed<int>(config, "SSE_POLL_INTERVAL_MS", int.TryParse, v => live.PollIntervalMs = v, errors);
        BindParsed<int>(config, "SSE_HEARTBEAT_SECONDS", int.TryParse, v => live.HeartbeatSeconds = v, errors);
        BindParsed<int>(config, "SSE_REAUTHORIZE_SECONDS", int.TryParse, v => live.ReauthorizeSeconds = v, errors);
        BindParsed<int>(config, "EVENT_UPDATES_RETENTION_HOURS", int.TryParse, v => live.RetentionHours = v, errors);

        // Redis settings are read (and validated) only when Redis is the configured queue.
        RedisSettings? redis = null;
        if (backend.QueueProvider == "redis-streams")
        {
            redis = new RedisSettings();
            Bind(config, "REDIS_CONNECTION", v => redis.Connection = v, errors);
            Bind(config, "REDIS_USER", v => redis.User = v, errors);
            Bind(config, "REDIS_PASSWORD", v => redis.Password = v, errors);
            BindParsed<bool>(config, "REDIS_SSL", bool.TryParse, v => redis.Ssl = v, errors);
            BindParsed<int>(config, "REDIS_CONNECT_TIMEOUT_MS", int.TryParse, v => redis.ConnectTimeoutMs = v, errors);
            BindParsed<int>(config, "REDIS_OPERATION_TIMEOUT_MS", int.TryParse, v => redis.OperationTimeoutMs = v, errors);
            Bind(config, "REDIS_EVENTS_STREAM", v => redis.EventsStream = v, errors);
            Bind(config, "REDIS_DLQ_STREAM", v => redis.EventsDlqStream = v, errors);
            Bind(config, "REDIS_RESULTS_STREAM", v => redis.ResultsStream = v, errors);
            Bind(config, "REDIS_RESULTS_DLQ_STREAM", v => redis.ResultsDlqStream = v, errors);
            Bind(config, "REDIS_AI_GROUP", v => redis.AiGroup = v, errors);
            BindParsed<bool>(config, "REDIS_CREATE_AI_GROUP", bool.TryParse, v => redis.CreateAiGroup = v, errors);
            Bind(config, "REDIS_BACKEND_GROUP", v => redis.BackendGroup = v, errors);
            BindParsed<int>(config, "REDIS_STREAM_MAXLEN", int.TryParse, v => redis.StreamMaxLength = v, errors);
            Bind(config, "REDIS_UPDATES_CHANNEL", v => redis.UpdatesChannel = v, errors);
            BindParsed<bool>(config, "RESULTS_CONSUMER_ENABLED", bool.TryParse, v => redis.ResultsConsumerEnabled = v, errors);
            BindParsed<int>(config, "RESULTS_CLAIM_IDLE_MS", int.TryParse, v => redis.ClaimIdleMs = v, errors);
            BindParsed<int>(config, "RESULTS_CLAIM_INTERVAL_MS", int.TryParse, v => redis.ClaimIntervalMs = v, errors);
            BindParsed<int>(config, "RESULTS_MAX_DELIVERIES", int.TryParse, v => redis.MaxDeliveries = v, errors);
            BindParsed<int>(config, "RESULTS_READ_BATCH", int.TryParse, v => redis.ReadBatch = v, errors);
            BindParsed<int>(config, "RESULTS_POLL_MS", int.TryParse, v => redis.PollMs = v, errors);
            if (redis.Connection.Contains("password=", StringComparison.OrdinalIgnoreCase))
                errors.Add("REDIS_CONNECTION must not contain the password; use REDIS_PASSWORD");
            var streams = new[] { redis.EventsStream, redis.EventsDlqStream, redis.ResultsStream, redis.ResultsDlqStream };
            if (streams.Distinct(StringComparer.Ordinal).Count() != streams.Length)
                errors.Add("REDIS_EVENTS_STREAM, REDIS_DLQ_STREAM, REDIS_RESULTS_STREAM and REDIS_RESULTS_DLQ_STREAM must all differ");
        }

        var session = new SessionOptions();
        BindParsed<double>(config, "SESSION_MAX_DURATION_HOURS", TryDouble, v => session.MaxDurationHours = v, errors);
        BindParsed<int>(config, "SESSION_UPLOAD_GRACE_SECONDS", int.TryParse, v => session.UploadGraceSeconds = v, errors);
        BindParsed<double>(config, "SESSION_TOKEN_TTL_AFTER_END_HOURS", TryDouble, v => session.TokenTtlAfterEndHours = v, errors);
        BindParsed<int>(config, "SESSION_IDEMPOTENCY_WINDOW_MINUTES", int.TryParse, v => session.IdempotencyWindowMinutes = v, errors);
        var tokenSecret = ReadTokenSecret(config, environment, errors);

        var policy = new RecordingPolicyOptions();
        BindParsed<double>(config, "RECORDING_PRE_EVENT_SECONDS", TryDouble, v => policy.PreEventSeconds = v, errors);
        BindParsed<double>(config, "RECORDING_SILENCE_SECONDS", TryDouble, v => policy.SilenceSeconds = v, errors);
        BindParsed<double>(config, "RECORDING_MAX_EVENT_SECONDS", TryDouble, v => policy.MaxEventSeconds = v, errors);
        BindParsed<double>(config, "RECORDING_SPLIT_OVERLAP_SECONDS", TryDouble, v => policy.SplitOverlapSeconds = v, errors);
        BindParsed<bool>(config, "RECORDING_UPLOAD_VIDEO", bool.TryParse, v => policy.UploadVideo = v, errors);
        BindParsed<long>(config, "RECORDING_MAX_UPLOAD_BYTES", long.TryParse, v => policy.MaxUploadBytes = v, errors);

        var groups = new List<(string, object)> { ("backend", backend), ("SESSION_*", session), ("RECORDING_*", policy), ("AUDIO_*/EVENT_*", events), ("SSE_*/EVENT_UPDATES_*", live) };
        if (redis is not null) groups.Add(("REDIS_*/RESULTS_*", redis));
        foreach (var (name, options) in groups)
        {
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
                errors.AddRange(results.Select(r => $"{name}: {r.ErrorMessage}"));
        }
        if (policy.SplitOverlapSeconds >= policy.MaxEventSeconds)
            errors.Add("RECORDING_SPLIT_OVERLAP_SECONDS must be smaller than RECORDING_MAX_EVENT_SECONDS");
        if (!Directory.Exists(backend.ContractsSchemaDirectory))
            errors.Add($"CONTRACTS_SCHEMA_DIR does not exist: {backend.ContractsSchemaDirectory}");

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid configuration:\n  - " + string.Join("\n  - ", errors));

        services.AddSingleton(backend);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(session));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(policy));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(events));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(live));
        return (backend, tokenSecret!, redis);
    }

    /// <summary>
    /// SESSION_TOKEN_SECRET (base64, at least 32 bytes). Required outside Development/Testing; otherwise a
    /// random per-process secret is used (idempotent replays then do not survive a restart).
    /// The value is never logged or included in error messages.
    /// </summary>
    private static SessionTokenSecret? ReadTokenSecret(IConfiguration config, IHostEnvironment environment, List<string> errors)
    {
        var raw = config["SESSION_TOKEN_SECRET"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                return new SessionTokenSecret(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), isEphemeral: true);
            errors.Add("SESSION_TOKEN_SECRET is required outside Development (base64 of at least 32 random bytes)");
            return null;
        }
        byte[] key;
        try
        {
            key = Convert.FromBase64String(raw.Trim());
        }
        catch (FormatException)
        {
            errors.Add("SESSION_TOKEN_SECRET must be base64");
            return null;
        }
        if (key.Length < SessionTokenSecret.MinimumBytes)
        {
            errors.Add($"SESSION_TOKEN_SECRET must decode to at least {SessionTokenSecret.MinimumBytes} bytes");
            return null;
        }
        return new SessionTokenSecret(key, isEphemeral: false);
    }

    private delegate bool TryParse<T>(string value, out T result);

    private static bool TryDouble(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static void Bind(IConfiguration config, string key, Action<string> set, List<string> errors)
    {
        var value = config[key];
        if (!string.IsNullOrWhiteSpace(value)) set(value.Trim());
    }

    private static void BindParsed<T>(IConfiguration config, string key, TryParse<T> parse, Action<T> set, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return;
        if (parse(value.Trim(), out var parsed)) set(parsed);
        else errors.Add($"{key}: '{value}' is not a valid {typeof(T).Name}");
    }
}
