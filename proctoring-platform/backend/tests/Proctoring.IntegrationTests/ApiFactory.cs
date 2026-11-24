using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Proctoring.IntegrationTests;

/// <summary>
/// Runs the real application in memory (WebApplicationFactory) against its own temporary SQLite file,
/// with a controllable clock and a log sink the tests can inspect.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DatabasePath { get; }
    public string AudioStorePath { get; }
    public string QueueDirectory { get; }
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
    public CapturingLoggerProvider Logs { get; } = new();
    private readonly Dictionary<string, string> _settings;

    private readonly string _environment;

    /// <summary>Optional extra service registrations (e.g. a failing dependency).</summary>
    public Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? ConfigureServices { get; init; }

    public ApiFactory(string? databasePath = null, Dictionary<string, string>? settings = null, string environment = "Testing")
    {
        _environment = environment;
        DatabasePath = databasePath ?? Path.Combine(Path.GetTempPath(), $"proctoring-it-{Guid.NewGuid():N}.db");
        AudioStorePath = DatabasePath + ".audio";
        QueueDirectory = DatabasePath + ".queue";
        _settings = new Dictionary<string, string>
        {
            ["DATABASE_PATH"] = DatabasePath,
            ["DATABASE_MIGRATE_ON_START"] = "true",
            ["CORS_ORIGINS"] = "http://localhost:5173",
            ["LOG_FORMAT"] = "json",
            ["SESSION_MAX_DURATION_HOURS"] = "6",
            ["SESSION_TOKEN_TTL_AFTER_END_HOURS"] = "24",
            ["AUDIO_STORE_PATH"] = AudioStorePath,
            ["QUEUE_LOCAL_DIRECTORY"] = QueueDirectory,
            ["EVENT_MAINTENANCE_ENABLED"] = "false", // tests run maintenance explicitly
        };
        foreach (var (k, v) in settings ?? []) _settings[k] = v;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment); // never "Development", so no .env loading
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ILoggerProvider>(Logs);
            ConfigureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
    }

    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(DatabasePath); } catch (IOException) { /* best effort */ }
        foreach (var dir in new[] { AudioStorePath, QueueDirectory })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

/// <summary>Collects every log message and scope value written during a test.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            owner.Entries.Enqueue($"scope: {Flatten(state)}");
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue($"{level}: {formatter(state, ex)} {Flatten(state)} {ex}");

        private static string Flatten(object? state) => state is IEnumerable<KeyValuePair<string, object>> kv
            ? string.Join(" ", kv.Select(p => $"{p.Key}={p.Value}"))
            : state?.ToString() ?? "";
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
