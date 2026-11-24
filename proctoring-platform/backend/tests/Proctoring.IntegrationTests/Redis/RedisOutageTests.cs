using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Proctoring.Application.Events;
using Proctoring.Tests.Shared;
using StackExchange.Redis;

namespace Proctoring.IntegrationTests.Redis;

/// <summary>
/// Redis outages, restarts, credentials and configuration validation. Each test gets its own password-protected,
/// append-only Redis on a fixed port that it can stop and start.
/// </summary>
public sealed class RedisOutageTests : IAsyncLifetime
{
    private readonly PrivateRedis _redis = new();
    private readonly RedisNames _n = RedisNames.New();
    private readonly List<ApiFactory> _factories = [];

    public Task InitializeAsync() => _redis.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var f in _factories)
        {
            f.Dispose();
            f.DeleteDatabase();
        }
        await _redis.DisposeAsync();
    }

    private (ApiFactory Factory, TestApi Api) Start(Dictionary<string, string>? extra = null, string password = PrivateRedis.Password)
    {
        var settings = _n.ApiSettings(_redis.Endpoint, extra);
        settings["REDIS_PASSWORD"] = password;
        var factory = new ApiFactory(settings: settings);
        _factories.Add(factory);
        return (factory, new TestApi(factory.CreateClient(), factory));
    }

    private static async Task<(HttpStatusCode Status, string Body)> HealthAsync(TestApi api, string path)
    {
        var r = await api.Client.GetAsync(path);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    private static string RedisCheck(string body) =>
        (string)JsonNode.Parse(body)!["checks"]!.AsArray().Single(c => (string?)c!["name"] == "redis")!["status"]!;

    private static void AssertNoCredentials(string text)
    {
        Assert.DoesNotContain(PrivateRedis.Password, text);
        Assert.DoesNotContain("password=", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readiness_checks_redis_with_credentials_without_ever_exposing_them()
    {
        var (factory, api) = Start();
        var ready = await HealthAsync(api, "/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.Equal("healthy", RedisCheck(ready.Body));
        AssertNoCredentials(ready.Body);

        await api.UploadAsync((await api.NewSessionAsync()).Id, (await api.NewSessionAsync()).Token, Guid.NewGuid()); // some traffic
        AssertNoCredentials(string.Join("\n", factory.Logs.Entries));
    }

    [Fact]
    public async Task Wrong_password_makes_readiness_fail_without_revealing_the_password()
    {
        var (_, api) = Start(password: "definitely-wrong-password-123");
        var ready = await HealthAsync(api, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.Equal("unhealthy", RedisCheck(ready.Body));
        Assert.Contains("Redis is not reachable", ready.Body);
        Assert.DoesNotContain("definitely-wrong-password-123", ready.Body);
        Assert.Equal(HttpStatusCode.OK, (await HealthAsync(api, "/health/live")).Status);
    }

    [Fact]
    public async Task Outage_keeps_uploads_safe_and_everything_resumes_after_Redis_returns()
    {
        var (factory, api) = Start();
        var (sid, token) = await api.NewSessionAsync();
        var before = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await api.UploadAsync(sid, token, before)).StatusCode);

        await _redis.StopAsync();

        // Upload during the outage: stored (RECEIVED), 503 with Retry-After, no fallback, no data loss.
        var during = Guid.NewGuid();
        var started = DateTime.UtcNow;
        var response = await api.UploadAsync(sid, token, during, sequence: 1);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "upload must not hang while Redis is down");
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("https://proctoring.local/errors/service-unavailable", (string?)problem["type"]);
        Assert.Equal("RECEIVED", (string?)problem["event"]!["status"]);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal("RECEIVED", await api.StatusAsync(sid, token, during));
        Assert.False(Directory.Exists(factory.QueueDirectory) && Directory.GetFiles(factory.QueueDirectory).Length > 0, "no silent fallback to the local queue");

        // Readiness reports the outage; liveness does not (the process is fine).
        var ready = await HealthAsync(api, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.Equal("unhealthy", RedisCheck(ready.Body));
        AssertNoCredentials(ready.Body);
        Assert.Equal(HttpStatusCode.OK, (await HealthAsync(api, "/health/live")).Status);
        // Reading state still works from SQLite.
        Assert.Equal("QUEUED", await api.StatusAsync(sid, token, before));

        await _redis.RestartAsync();
        await Eventually.GetAsync(async () => (await HealthAsync(api, "/health/ready")).Status, s => s == HttpStatusCode.OK, 30, "ready again");

        // Maintenance publishes the event stored during the outage.
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<EventMaintenance>().RunOnceAsync(default);
        Assert.Equal("QUEUED", await api.StatusAsync(sid, token, during));

        await using var mux = await _redis.ConnectAsync();
        var db = mux.GetDatabase();
        var entries = await db.StreamRangeAsync(_n.Events);
        Assert.Equal([before.ToString(), during.ToString()], entries.Select(e => e["eventId"].ToString()));

        // The results consumer reconnected by itself.
        await db.StreamAddAsync(_n.Results, TestMessages.Envelope(TestMessages.Completed(sid, during)).Select(f => new NameValueEntry(f.Key, f.Value)).ToArray());
        await Eventually.GetAsync(() => api.StatusAsync(sid, token, during), s => s == "COMPLETED", 45, "result applied after outage");
        AssertNoCredentials(string.Join("\n", factory.Logs.Entries));
    }

    [Fact]
    public async Task Stream_entries_groups_and_pending_work_survive_a_Redis_restart()
    {
        var (_, api) = Start();
        var (sid, token) = await api.NewSessionAsync();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await api.UploadAsync(sid, token, id)).StatusCode);

        // A processing worker reads the event but has not finished when Redis restarts.
        await using (var mux = await _redis.ConnectAsync())
            Assert.Single(await mux.GetDatabase().StreamReadGroupAsync(_n.Events, _n.AiGroup, "worker-1", StreamPosition.NewMessages, 10));

        await _redis.StopAsync();
        await _redis.RestartAsync();

        await using var after = await _redis.ConnectAsync();
        var db = after.GetDatabase();
        Assert.Equal(id.ToString(), Assert.Single(await db.StreamRangeAsync(_n.Events))["eventId"].ToString());
        var pending = await db.StreamPendingAsync(_n.Events, _n.AiGroup);
        Assert.Equal(1, pending.PendingMessageCount); // the worker (or another) can still reclaim it

        await db.StreamAddAsync(_n.Results, TestMessages.Envelope(TestMessages.Completed(sid, id)).Select(f => new NameValueEntry(f.Key, f.Value)).ToArray());
        await Eventually.GetAsync(() => api.StatusAsync(sid, token, id), s => s == "COMPLETED", 45, "result applied after restart");
    }

    [Fact]
    public async Task Application_starts_while_Redis_is_down_and_becomes_ready_when_it_comes_up()
    {
        await _redis.StopAsync();
        var (_, api) = Start();
        Assert.Equal(HttpStatusCode.OK, (await HealthAsync(api, "/health/live")).Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await HealthAsync(api, "/health/ready")).Status);

        await _redis.RestartAsync();
        await Eventually.GetAsync(async () => (await HealthAsync(api, "/health/ready")).Status, s => s == HttpStatusCode.OK, 30, "ready");
    }

    [Theory]
    [InlineData("QUEUE_PROVIDER", "kafka", "QueueProvider")]
    [InlineData("REDIS_CONNECTION", "localhost:6379,password=secret-in-connection", "REDIS_CONNECTION must not contain the password")]
    [InlineData("REDIS_RESULTS_STREAM", "same-stream", "must all differ")]
    [InlineData("RESULTS_MAX_DELIVERIES", "0", "REDIS_*/RESULTS_*")]
    [InlineData("SSE_HEARTBEAT_SECONDS", "abc", "SSE_HEARTBEAT_SECONDS")]
    public void Invalid_queue_configuration_fails_at_startup(string key, string value, string expected)
    {
        var settings = _n.ApiSettings(_redis.Endpoint);
        settings[key] = value;
        if (key == "REDIS_RESULTS_STREAM") settings["REDIS_EVENTS_STREAM"] = value;
        using var factory = new ApiFactory(settings: settings);
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = ex.ToString();
        Assert.Contains(expected, message);
        Assert.DoesNotContain("secret-in-connection", message);
        factory.DeleteDatabase();
    }
}
