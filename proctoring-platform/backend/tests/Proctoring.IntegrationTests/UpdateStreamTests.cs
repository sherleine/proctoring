using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Proctoring.Application.Events;
using Proctoring.Infrastructure.Validation;

namespace Proctoring.IntegrationTests;

/// <summary>GET /api/v1/sessions/{id}/updates (Server-Sent Events) with the local queue provider.</summary>
public sealed class UpdateStreamTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Schemas = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private readonly ApiFactory _factory;
    private readonly TestApi _api;

    public UpdateStreamTests()
    {
        _factory = new ApiFactory(settings: new()
        {
            ["RECORDING_MAX_UPLOAD_BYTES"] = "400000",
            ["SSE_POLL_INTERVAL_MS"] = "100",
            ["SSE_HEARTBEAT_SECONDS"] = "1",
            ["SSE_REAUTHORIZE_SECONDS"] = "5",
        });
        _api = new TestApi(_factory.CreateClient(), _factory);
    }

    public void Dispose()
    {
        _api.Client.Dispose();
        _factory.Dispose();
        _factory.DeleteDatabase();
    }

    private static void AssertEventSchema(JsonNode body)
    {
        var errors = Schemas.Validate("event-status.v1.json", body);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
    }

    private static Func<SseMessage, bool> StatusOf(Guid eventId, string status) =>
        m => m.Event == "event.updated" && (string?)m.Json["eventId"] == eventId.ToString() && (string?)m.Json["status"] == status;

    // ------------------------------------------------------------------ authentication and isolation

    [Fact]
    public async Task Stream_requires_the_session_token_and_reveals_nothing_about_other_sessions()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var (otherSid, otherToken) = await _api.NewSessionAsync();

        var none = await SseStream.RequestAsync(_api.Client, sid, null);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("application/problem+json", none.Content.Headers.ContentType?.MediaType);

        var malformed = await SseStream.RequestAsync(_api.Client, sid, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);

        var wrong = await SseStream.RequestAsync(_api.Client, sid, otherToken);
        var unknown = await SseStream.RequestAsync(_api.Client, "ses_DOESNOTEXIST000000000000", token);
        var badId = await SseStream.RequestAsync(_api.Client, "nope", token);
        var details = new List<string?>();
        foreach (var r in new[] { wrong, unknown, badId })
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            var body = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
            Assert.Equal("https://proctoring.local/errors/not-found", (string?)body["type"]);
            details.Add((string?)body["detail"]);
        }
        // Another session's token and an unknown session are indistinguishable.
        Assert.Equal(details[0], details[1]);
        Assert.NotNull(otherSid);
    }

    [Fact]
    public async Task A_stream_only_carries_events_of_its_own_session()
    {
        var (a, tokenA) = await _api.NewSessionAsync();
        var (b, tokenB) = await _api.NewSessionAsync();
        await using var streamB = await SseStream.OpenAsync(_api.Client, b, tokenB);
        Assert.Equal("ready", (await streamB.NextEventAsync())!.Event);

        var eventA = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await _api.UploadAsync(a, tokenA, eventA)).StatusCode);
        var eventB = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await _api.UploadAsync(b, tokenB, eventB)).StatusCode);

        var m = await streamB.WaitForAsync(StatusOf(eventB, "QUEUED"));
        Assert.Equal(b, (string?)m.Json["sessionId"]);
        Assert.DoesNotContain(eventA.ToString(), streamB.Raw);
        Assert.DoesNotContain(a, streamB.Raw);
    }

    // ------------------------------------------------------------------ live updates

    [Fact]
    public async Task Ready_then_status_changes_with_increasing_ids_and_the_full_event_state()
    {
        var (sid, token) = await _api.NewSessionAsync();
        await using var stream = await SseStream.OpenAsync(_api.Client, sid, token);
        Assert.Equal("no-store", stream.Response.Headers.CacheControl?.ToString());

        var ready = (await stream.NextEventAsync())!;
        Assert.Equal("ready", ready.Event);
        Assert.Equal("0", ready.Id);
        Assert.Equal(sid, (string?)ready.Json["sessionId"]);

        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await _api.UploadAsync(sid, token, id)).StatusCode);
        var queued = await stream.WaitForAsync(StatusOf(id, "QUEUED"));
        AssertEventSchema(queued.Json);
        Assert.True(long.Parse(queued.Id!) > 0);

        // Polling fallback: GET returns exactly the state the stream delivered.
        var polled = await _api.GetEventAsync(sid, token, id);
        Assert.True(JsonNode.DeepEquals(polled, queued.Json), $"{polled} != {queued.Json}");

        // No secrets or internal details in the stream.
        foreach (var leak in new[] { token, _factory.AudioStorePath.Replace("\\", "\\\\"), "storage", ".wav", "file:", "Exception" })
            Assert.DoesNotContain(leak, stream.Raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Last_Event_ID_resumes_after_the_last_delivered_update_without_gaps()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var first = Guid.NewGuid();
        await _api.UploadAsync(sid, token, first);

        string lastId;
        await using (var stream = await SseStream.OpenAsync(_api.Client, sid, token, lastEventId: "0"))
        {
            Assert.Equal("ready", (await stream.NextEventAsync())!.Event);
            lastId = (await stream.WaitForAsync(StatusOf(first, "QUEUED"))).Id!;
        }

        // Changes while the client was disconnected...
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        await _api.UploadAsync(sid, token, second, sequence: 1);
        await _api.UploadAsync(sid, token, third, sequence: 2);

        // ...are replayed after reconnecting with Last-Event-ID, and nothing before it is repeated.
        await using var resumed = await SseStream.OpenAsync(_api.Client, sid, token, lastEventId: lastId);
        var ready = (await resumed.NextEventAsync())!;
        Assert.Equal("ready", ready.Event);
        Assert.Equal(lastId, ready.Id);
        var a = await resumed.WaitForAsync(StatusOf(second, "QUEUED"));
        var b = await resumed.WaitForAsync(StatusOf(third, "QUEUED"));
        Assert.True(long.Parse(a.Id!) > long.Parse(lastId) && long.Parse(b.Id!) > long.Parse(a.Id!));
        Assert.DoesNotContain(first.ToString(), resumed.Raw);
    }

    [Fact]
    public async Task Unusable_or_pruned_Last_Event_ID_asks_the_client_to_resync()
    {
        var (sid, token) = await _api.NewSessionAsync();
        await using (var invalid = await SseStream.OpenAsync(_api.Client, sid, token, lastEventId: "abc"))
            Assert.Equal("resync", (await invalid.NextEventAsync())!.Event);

        await _api.UploadAsync(sid, token, Guid.NewGuid());
        _factory.Clock.Advance(TimeSpan.FromHours(2)); // within the session lifetime
        var recent = Guid.NewGuid();
        await _api.UploadAsync(sid, token, recent, sequence: 1);
        await using (var scope = _factory.Services.CreateAsyncScope())
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<IEventUpdateRepository>()
                .DeleteOlderThanAsync(_factory.Clock.GetUtcNow().UtcDateTime.AddHours(-1), default));

        // Entries after id 1 were pruned: the client must reload GET /events; the stream then continues live.
        await using var stream = await SseStream.OpenAsync(_api.Client, sid, token, lastEventId: "1");
        var resync = (await stream.NextEventAsync())!;
        Assert.Equal("resync", resync.Event);
        var later = Guid.NewGuid();
        await _api.UploadAsync(sid, token, later, sequence: 2);
        await stream.WaitForAsync(StatusOf(later, "QUEUED"));
        Assert.DoesNotContain(recent.ToString(), stream.Raw); // older than the resync point: not replayed
    }

    [Fact]
    public async Task Idle_streams_get_heartbeats()
    {
        var (sid, token) = await _api.NewSessionAsync();
        await using var stream = await SseStream.OpenAsync(_api.Client, sid, token);
        Assert.Equal("ready", (await stream.NextEventAsync())!.Event);
        _factory.Clock.Advance(TimeSpan.FromSeconds(2));
        var m = (await stream.NextAsync())!;
        Assert.Equal("heartbeat", m.Comment);
        Assert.StartsWith("retry: 3000\n", stream.Raw);
    }

    [Fact]
    public async Task Stream_ends_when_the_session_token_expires()
    {
        var (sid, token) = await _api.NewSessionAsync();
        await using var stream = await SseStream.OpenAsync(_api.Client, sid, token);
        Assert.Equal("ready", (await stream.NextEventAsync())!.Event);

        Assert.Equal(HttpStatusCode.OK, (await _api.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sid}/end", token)).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromHours(25)); // past SESSION_TOKEN_TTL_AFTER_END_HOURS

        var end = await stream.WaitForAsync(m => m.Event == "end");
        Assert.Equal("token-expired", (string?)end.Json["reason"]);
        Assert.Null(await stream.NextEventAsync()); // server closed the stream
        Assert.Equal(HttpStatusCode.Unauthorized, (await SseStream.RequestAsync(_api.Client, sid, token)).StatusCode);
    }

    [Fact]
    public async Task Client_disconnect_ends_the_stream_cleanly()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var stream = await SseStream.OpenAsync(_api.Client, sid, token);
        Assert.Equal("ready", (await stream.NextEventAsync())!.Event);
        await stream.DisposeAsync();
        await Task.Delay(500);

        // The server keeps working and logged no stream error.
        Assert.Equal(HttpStatusCode.Accepted, (await _api.UploadAsync(sid, token, Guid.NewGuid())).StatusCode);
        Assert.DoesNotContain(_factory.Logs.Entries, e => e.Contains("Update stream for session"));
    }
}
