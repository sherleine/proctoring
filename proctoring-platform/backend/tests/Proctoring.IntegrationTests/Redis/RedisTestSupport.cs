using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Proctoring.Infrastructure.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Proctoring.IntegrationTests.Redis;

/// <summary>One real Redis (Docker, redis:7.4-alpine) shared by the tests of the "redis" collection.
/// Tests isolate themselves with unique stream/group names (<see cref="RedisNames"/>).</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    public const string Image = "redis:7.4-alpine";
    private readonly RedisContainer _container = new RedisBuilder().WithImage(Image).Build();

    public string Endpoint => _container.GetConnectionString();
    public IConnectionMultiplexer Mux { get; private set; } = null!;
    public IDatabase Db => Mux.GetDatabase();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Mux = await ConnectionMultiplexer.ConnectAsync(Endpoint);
    }

    public async Task DisposeAsync()
    {
        await Mux.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;

/// <summary>Unique stream, group and channel names for one test.</summary>
public sealed record RedisNames(string Prefix)
{
    public static RedisNames New() => new($"t{Guid.NewGuid():N}"[..13]);
    public string Events => $"{Prefix}.audio.events.v1";
    public string EventsDlq => $"{Prefix}.audio.events.dlq.v1";
    public string Results => $"{Prefix}.audio.results.v1";
    public string ResultsDlq => $"{Prefix}.audio.results.dlq.v1";
    public string AiGroup => $"{Prefix}-ai";
    public string BackendGroup => $"{Prefix}-backend";
    public string Channel => $"{Prefix}.updates";

    /// <summary>API settings for QUEUE_PROVIDER=redis-streams with these names.</summary>
    public Dictionary<string, string> ApiSettings(string endpoint, Dictionary<string, string>? extra = null)
    {
        var s = new Dictionary<string, string>
        {
            ["QUEUE_PROVIDER"] = "redis-streams",
            ["REDIS_CONNECTION"] = endpoint,
            ["REDIS_CONNECT_TIMEOUT_MS"] = "2000",
            ["REDIS_OPERATION_TIMEOUT_MS"] = "2000",
            ["REDIS_EVENTS_STREAM"] = Events,
            ["REDIS_DLQ_STREAM"] = EventsDlq,
            ["REDIS_RESULTS_STREAM"] = Results,
            ["REDIS_RESULTS_DLQ_STREAM"] = ResultsDlq,
            ["REDIS_AI_GROUP"] = AiGroup,
            ["REDIS_BACKEND_GROUP"] = BackendGroup,
            ["REDIS_UPDATES_CHANNEL"] = Channel,
            ["RESULTS_POLL_MS"] = "50",
            ["RESULTS_CLAIM_IDLE_MS"] = "1000",
            ["SSE_POLL_INTERVAL_MS"] = "200",
            ["RECORDING_MAX_UPLOAD_BYTES"] = "400000",
        };
        foreach (var (k, v) in extra ?? []) s[k] = v;
        return s;
    }

    public RedisConnection Connection(string endpoint) =>
        new(new RedisSettings { Connection = endpoint, EventsStream = Events, ResultsStream = Results, ConnectTimeoutMs = 2000, OperationTimeoutMs = 2000 },
            NullLogger<RedisConnection>.Instance);
}

/// <summary>
/// A private Redis for tests that stop, restart or password-protect it: fixed host port (so clients reconnect to
/// the same address after a restart), append-only persistence, and a password.
/// </summary>
public sealed class PrivateRedis : IAsyncDisposable
{
    public const string Password = "it-redis-pass-7f3c2a1e9b";
    private readonly IContainer _container;

    public PrivateRedis()
    {
        Port = FreePort();
        _container = new ContainerBuilder()
            .WithImage(RedisFixture.Image)
            .WithPortBinding(Port, 6379)
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always", "--requirepass", Password)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
    }

    public int Port { get; }
    public string Endpoint => $"127.0.0.1:{Port}";

    public async Task StartAsync() => await _container.StartAsync();

    public async Task StopAsync() => await _container.StopAsync();

    /// <summary>Starts the stopped container again (same port, same data) and waits until it answers.</summary>
    public async Task RestartAsync()
    {
        await _container.StartAsync();
        await WaitUntilReadyAsync();
    }

    public async Task<IConnectionMultiplexer> ConnectAsync() =>
        await ConnectionMultiplexer.ConnectAsync($"{Endpoint},password={Password},abortConnect=false,connectTimeout=2000");

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await using var mux = await ConnectAsync();
                await mux.GetDatabase().PingAsync();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public static class Eventually
{
    /// <summary>Polls <paramref name="condition"/> until it holds or the timeout elapses.</summary>
    public static async Task<T> GetAsync<T>(Func<Task<T>> probe, Func<T, bool> condition, double timeoutSeconds = 15, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        T value;
        while (!condition(value = await probe()))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what ?? "condition"}; last value: {value}");
            await Task.Delay(100);
        }
        return value;
    }
}
