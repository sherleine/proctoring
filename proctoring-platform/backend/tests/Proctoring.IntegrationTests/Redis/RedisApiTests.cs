using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Proctoring.Application.Events;
using Proctoring.Domain.Events;
using Proctoring.Infrastructure.Persistence;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;
using StackExchange.Redis;

namespace Proctoring.IntegrationTests.Redis;

/// <summary>The API with QUEUE_PROVIDER=redis-streams against a real Redis.</summary>
[Collection("redis")]
public sealed class RedisApiTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Schemas = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private readonly RedisFixture _redis;
    private readonly RedisNames _n = RedisNames.New();
    private readonly ApiFactory _factory;
    private readonly TestApi _api;
    private readonly List<ApiFactory> _extra = [];

    public RedisApiTests(RedisFixture redis)
    {
        _redis = redis;
        _factory = new ApiFactory(settings: _n.ApiSettings(redis.Endpoint));
        _api = new TestApi(_factory.CreateClient(), _factory);
    }

    public void Dispose()
    {
        _api.Client.Dispose();
        foreach (var f in _extra) f.Dispose();
        _factory.Dispose();
        _factory.DeleteDatabase();
    }

    private IDatabase Db => _redis.Db;

    private Task<RedisValue> PublishResultAsync(JsonObject payload, string? messageType = null) =>
        Db.StreamAddAsync(_n.Results, TestMessages.Envelope(payload, messageType).Select(f => new NameValueEntry(f.Key, f.Value)).ToArray());

    private static Dictionary<string, string> Fields(StreamEntry e) => e.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

    private Task<string> WaitForStatusAsync(string sid, string token, Guid id, string status) =>
        Eventually.GetAsync(() => _api.StatusAsync(sid, token, id), s => s == status, what: $"event {id} -> {status}");

    private async Task<long> BackendPendingAsync() => (await Db.StreamPendingAsync(_n.Results, _n.BackendGroup)).PendingMessageCount;

    private async Task<(string Sid, string Token, Guid Id)> QueuedEventAsync()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var id = Guid.NewGuid();
        var response = await _api.UploadAsync(sid, token, id);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (sid, token, id);
    }

    private static void AssertValid(string schema, JsonNode node)
    {
        var errors = Schemas.Validate(schema, node);
        Assert.True(errors.Count == 0, $"{schema}: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
    }

    // ------------------------------------------------------------------ publishing

    [Fact]
    public async Task Upload_publishes_exactly_the_contract_envelope_with_an_audio_reference_and_no_audio_bytes()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var wav = TestAudio.Wav(1.5);
        var id = Guid.NewGuid();
        var response = await _api.UploadAsync(sid, token, id, wav);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("QUEUED", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["status"]);

        var entry = Assert.Single(await Db.StreamRangeAsync(_n.Events));
        var fields = Fields(entry);
        Assert.Equal(["correlationId", "eventId", "messageId", "messageType", "payload", "producedAt", "schemaVersion", "sessionId"], fields.Keys.Order());
        var envelope = new JsonObject();
        foreach (var (k, v) in fields) envelope[k] = v;
        AssertValid("stream-envelope.v1.json", envelope);
        Assert.Equal("audio.event.submitted", fields["messageType"]);
        Assert.Equal(id.ToString(), fields["eventId"]);
        Assert.Equal(sid, fields["sessionId"]);

        var payload = JsonNode.Parse(fields["payload"])!;
        AssertValid("audio-event-message.v1.json", payload);
        var audioRef = payload["audioRef"]!;
        Assert.Equal(TestAudio.Sha256(wav), (string?)audioRef["sha256"]);
        Assert.Equal(wav.Length, (long)audioRef["sizeBytes"]!);
        Assert.Matches(@"^ses_[A-Za-z0-9]+/[a-f0-9]{32}\.wav$", (string?)audioRef["key"]);
        // A reference, never the audio itself.
        Assert.True(fields["payload"].Length < 4000, $"payload is {fields["payload"].Length} chars");
        Assert.DoesNotContain("RIFF", fields["payload"]);
        Assert.DoesNotContain(Convert.ToBase64String(wav[44..200]), fields["payload"]);

        // The processing group exists and starts at the first entry, so nothing published before the
        // processing service first starts is skipped.
        var group = Assert.Single(await Db.StreamGroupInfoAsync(_n.Events));
        Assert.Equal(_n.AiGroup, group.Name);
        Assert.Equal(0, group.PendingMessageCount);
        Assert.Equal("0-0", group.LastDeliveredId);
    }

    [Fact]
    public async Task Retried_upload_of_the_same_event_is_not_published_twice()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var wav = TestAudio.Wav(1.5);
        var id = Guid.NewGuid();
        var metadata = TestAudio.Metadata(wav, id, _factory.Clock.GetUtcNow().UtcDateTime);
        Assert.Equal(HttpStatusCode.Accepted, (await _api.UploadAsync(sid, token, id, wav, metadata: metadata)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.UploadAsync(sid, token, id, wav, metadata: metadata)).StatusCode);
        Assert.Equal(1, await Db.StreamLengthAsync(_n.Events));
    }

    // ------------------------------------------------------------------ results

    [Fact]
    public async Task Results_stream_moves_the_event_to_PROCESSING_then_COMPLETED_and_entries_are_acknowledged()
    {
        var (sid, token, id) = await QueuedEventAsync();

        await PublishResultAsync(TestMessages.Started(sid, id));
        await WaitForStatusAsync(sid, token, id, "PROCESSING");

        await PublishResultAsync(TestMessages.Completed(sid, id, "final transcript"));
        await WaitForStatusAsync(sid, token, id, "COMPLETED");

        var result = await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events/{id}/result", token);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("final transcript", (string?)JsonNode.Parse(await result.Content.ReadAsStringAsync())!["transcription"]!["text"]);
        await Eventually.GetAsync(BackendPendingAsync, p => p == 0, what: "results acknowledged");
        Assert.Equal(0, await Db.StreamLengthAsync(_n.ResultsDlq));
    }

    [Fact]
    public async Task Duplicate_results_are_acknowledged_and_never_replace_the_first_result()
    {
        var (sid, token, id) = await QueuedEventAsync();
        await PublishResultAsync(TestMessages.Completed(sid, id, "first"));
        await WaitForStatusAsync(sid, token, id, "COMPLETED");

        await PublishResultAsync(TestMessages.Completed(sid, id, "second"));
        await PublishResultAsync(TestMessages.Started(sid, id, 2));
        await PublishResultAsync(TestMessages.Failed(sid, id, "DEAD_LETTERED"));
        await Eventually.GetAsync(async () => (await Db.StreamGroupInfoAsync(_n.Results)).Single().EntriesRead ?? 0, n => n == 4, what: "all read");
        await Eventually.GetAsync(BackendPendingAsync, p => p == 0, what: "all acknowledged");

        var e = await _api.GetEventAsync(sid, token, id);
        Assert.Equal("COMPLETED", (string?)e["status"]);
        Assert.Equal("first", (string?)e["result"]!["transcription"]!["text"]);
        Assert.Equal(0, await Db.StreamLengthAsync(_n.ResultsDlq));
    }

    [Fact]
    public async Task Invalid_results_go_to_the_results_dead_letter_stream_and_change_nothing()
    {
        var (sid, token, id) = await QueuedEventAsync();
        var broken = TestMessages.Envelope(TestMessages.Completed(sid, id));
        broken.Remove("messageId");
        await Db.StreamAddAsync(_n.Results, broken.Select(f => new NameValueEntry(f.Key, f.Value)).ToArray());
        await PublishResultAsync(TestMessages.Completed(sid, Guid.NewGuid())); // unknown event
        var invalidPayload = TestMessages.Completed(sid, id);
        invalidPayload.Remove("speech");
        await PublishResultAsync(invalidPayload);

        var dlq = await Eventually.GetAsync(() => Db.StreamRangeAsync(_n.ResultsDlq), d => d.Length == 3, what: "3 dead letters");
        Assert.Contains("envelope violates", dlq[0]["dlq.reason"].ToString());
        Assert.Contains("Unknown event", dlq[1]["dlq.reason"].ToString());
        Assert.Contains("payload violates", dlq[2]["dlq.reason"].ToString());
        Assert.All(dlq, d => Assert.Equal(_n.BackendGroup, d["dlq.consumerGroup"].ToString()));
        await Eventually.GetAsync(BackendPendingAsync, p => p == 0, what: "acknowledged");
        Assert.Equal("QUEUED", await _api.StatusAsync(sid, token, id));
    }

    [Fact]
    public async Task Processing_failure_that_was_dead_lettered_by_the_processor_marks_the_event_DEAD_LETTERED()
    {
        var (sid, token, id) = await QueuedEventAsync();
        await PublishResultAsync(TestMessages.Failed(sid, id, ResultIngestionService.DeadLetteredCode, deliveryCount: 3));
        await WaitForStatusAsync(sid, token, id, "DEAD_LETTERED");
        var e = await _api.GetEventAsync(sid, token, id);
        Assert.Equal("DEAD_LETTERED", (string?)e["error"]!["code"]);
        Assert.Equal(3, (int)e["attempts"]!);
    }

    // ------------------------------------------------------------------ outbox recovery

    private async Task SetStatusAsync(Guid id, AudioEventStatus status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProctoringDbContext>();
        await db.AudioEvents.Where(e => e.EventId == id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, status).SetProperty(e => e.PublishLeaseUntil, (DateTime?)null));
    }

    private async Task RunMaintenanceAsync()
    {
        _factory.Clock.Advance(TimeSpan.FromMinutes(2)); // past EVENT_REPUBLISH_AFTER_SECONDS
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventMaintenance>().RunOnceAsync(default);
    }

    [Fact]
    public async Task Crash_before_XADD_is_recovered_by_maintenance_publishing_the_event()
    {
        var (sid, token, id) = await QueuedEventAsync();
        // Simulate: the row was committed as RECEIVED, then the process died before publishing.
        await SetStatusAsync(id, AudioEventStatus.Received);
        await Db.KeyDeleteAsync(_n.Events);

        await RunMaintenanceAsync();
        Assert.Equal("QUEUED", await _api.StatusAsync(sid, token, id));
        var entry = Assert.Single(await Db.StreamRangeAsync(_n.Events));
        Assert.Equal(id.ToString(), entry["eventId"].ToString());
    }

    [Fact]
    public async Task Crash_after_XADD_publishes_a_duplicate_that_processing_handles_idempotently()
    {
        var (sid, token, id) = await QueuedEventAsync();
        // Simulate: XADD succeeded but the process died before recording QUEUED.
        await SetStatusAsync(id, AudioEventStatus.Received);
        await RunMaintenanceAsync();

        var entries = await Db.StreamRangeAsync(_n.Events);
        Assert.Equal(2, entries.Length); // at-least-once: same eventId twice, different messageId
        Assert.All(entries, e => Assert.Equal(id.ToString(), e["eventId"].ToString()));
        Assert.NotEqual(entries[0]["messageId"], entries[1]["messageId"]);
        Assert.Equal("QUEUED", await _api.StatusAsync(sid, token, id));

        // A processor that analyses both copies produces two results; only the first is kept.
        await PublishResultAsync(TestMessages.Completed(sid, id, "copy 1"));
        await PublishResultAsync(TestMessages.Completed(sid, id, "copy 2"));
        await WaitForStatusAsync(sid, token, id, "COMPLETED");
        await Eventually.GetAsync(BackendPendingAsync, p => p == 0, what: "acknowledged");
        await Eventually.GetAsync(async () => (await Db.StreamGroupInfoAsync(_n.Results)).Single().EntriesRead ?? 0, n => n == 2, what: "both read");
        Assert.Equal("copy 1", (string?)(await _api.GetEventAsync(sid, token, id))["result"]!["transcription"]!["text"]);
    }

    // ------------------------------------------------------------------ health

    [Fact]
    public async Task Readiness_includes_a_healthy_redis_check()
    {
        var ready = await _api.Client.GetAsync("/health/ready");
        var body = JsonNode.Parse(await ready.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var redis = body["checks"]!.AsArray().Single(c => (string?)c!["name"] == "redis")!;
        Assert.Equal("healthy", (string?)redis["status"]);
    }

    // ------------------------------------------------------------------ live updates

    [Fact]
    public async Task Results_reach_open_SSE_streams()
    {
        var (sid, token, id) = await QueuedEventAsync();
        await using var stream = await SseStream.OpenAsync(_api.Client, sid, token);
        Assert.Equal("ready", (await stream.NextEventAsync())!.Event);

        await PublishResultAsync(TestMessages.Started(sid, id));
        await stream.WaitForAsync(m => (string?)m.Json["status"] == "PROCESSING");
        await PublishResultAsync(TestMessages.Completed(sid, id, "streamed"));
        var done = await stream.WaitForAsync(m => (string?)m.Json["status"] == "COMPLETED");
        Assert.Equal("streamed", (string?)done.Json["result"]!["transcription"]!["text"]);
    }

    [Fact]
    public async Task Update_signals_reach_SSE_streams_held_by_another_backend_instance()
    {
        // Instance B shares the database and Redis, runs no results consumer and polls only once a minute:
        // only the Redis pub/sub signal from instance A can deliver the update quickly.
        var b = new ApiFactory(_factory.DatabasePath, _n.ApiSettings(_redis.Endpoint, new()
        {
            ["RESULTS_CONSUMER_ENABLED"] = "false",
            ["SSE_POLL_INTERVAL_MS"] = "60000",
            ["SSE_HEARTBEAT_SECONDS"] = "300",
        }));
        _extra.Add(b);
        var clientB = b.CreateClient();

        var (sid, token, id) = await QueuedEventAsync();
        await using var stream = await SseStream.OpenAsync(clientB, sid, token);
        Assert.Equal("ready", (await stream.NextEventAsync())!.Event);
        await Task.Delay(1000); // let instance B subscribe to the update channel

        await PublishResultAsync(TestMessages.Completed(sid, id, "cross-instance")); // consumed by instance A
        var done = await stream.WaitForAsync(m => (string?)m.Json["status"] == "COMPLETED", timeoutSeconds: 20);
        Assert.Equal("cross-instance", (string?)done.Json["result"]!["transcription"]!["text"]);
    }
}
