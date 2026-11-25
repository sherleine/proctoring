using System.Net;
using System.Text.Json.Nodes;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;
using StackExchange.Redis;

namespace Proctoring.IntegrationTests.Redis;

/// <summary>
/// Contract 1.1 end to end over HTTP + a real Redis: speech event -> stream message -> AI result with observations ->
/// observation rows readable through the API.
/// </summary>
[Collection("redis")]
public sealed class RedisObservationTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Schemas = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private readonly RedisFixture _redis;
    private readonly RedisNames _n = RedisNames.New();
    private readonly ApiFactory _factory;
    private readonly TestApi _api;

    public RedisObservationTests(RedisFixture redis)
    {
        _redis = redis;
        _factory = new ApiFactory(settings: _n.ApiSettings(redis.Endpoint));
        _api = new TestApi(_factory.CreateClient(), _factory);
    }

    public void Dispose()
    {
        _api.Client.Dispose();
        _factory.Dispose();
        _factory.DeleteDatabase();
    }

    private Task<RedisValue> PublishResultAsync(JsonObject payload) =>
        _redis.Db.StreamAddAsync(_n.Results, TestMessages.Envelope(payload).Select(f => new NameValueEntry(f.Key, f.Value)).ToArray());

    private Task<string> WaitForStatusAsync(string sid, string token, Guid id, string status) =>
        Eventually.GetAsync(() => _api.StatusAsync(sid, token, id), s => s == status, what: $"event {id} -> {status}");

    private async Task<JsonNode> MessageForAsync(Guid eventId)
    {
        var entries = await _redis.Db.StreamRangeAsync(_n.Events);
        var entry = entries.Single(e => e.Values.Any(v => v.Name == "eventId" && v.Value == eventId.ToString()));
        return JsonNode.Parse(entry.Values.Single(v => v.Name == "payload").Value.ToString())!;
    }

    [Fact]
    public async Task Speech_event_flows_through_redis_and_its_observations_are_queryable()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var speechId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await _api.UploadAsync(sid, token, speechId)).StatusCode);
        var message = await MessageForAsync(speechId);
        Assert.Empty(Schemas.Validate("audio-event-message.v1.json", message));

        var result = TestMessages.Completed(sid, speechId);
        result["schemaVersion"] = "1.1";
        var obsId = Guid.NewGuid();
        result["observations"] = new JsonArray(new JsonObject
        {
            ["observationId"] = obsId.ToString(), ["sessionId"] = sid, ["eventId"] = speechId.ToString(),
            ["observationType"] = "MULTIPLE_SPEAKERS_DETECTED", ["startSeconds"] = 0.2, ["endSeconds"] = 1.4, ["confidence"] = 0.77,
            ["confidenceKind"] = "model-score", ["supportingEvidence"] = new JsonObject { ["speakers"] = 2 },
            ["modelName"] = "pyannote segmentation + CAM++", ["modelVersion"] = "test", ["category"] = "review",
            ["createdAt"] = "2026-11-21T10:00:05.000Z",
        });
        await PublishResultAsync(result);
        await WaitForStatusAsync(sid, token, speechId, "COMPLETED");

        var list = await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = JsonNode.Parse(await list.Content.ReadAsStringAsync())!;
        var item = Assert.Single(body["items"]!.AsArray())!;
        Assert.Equal(obsId.ToString(), (string)item["observationId"]!);
        Assert.Equal("MULTIPLE_SPEAKERS_DETECTED", (string)item["observationType"]!);
        Assert.Equal("review", (string)item["category"]!);
        Assert.Equal(1.2, (double)item["durationSeconds"]!, 3);
        Assert.Equal(2, (int)item["supportingEvidence"]!["speakers"]!);
        Assert.Contains("None of them is a determination of misconduct", (string)body["note"]!);
    }

    [Fact]
    public async Task Observations_need_the_session_token()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var (_, otherToken) = await _api.NewSessionAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", null)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", otherToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", token)).StatusCode);
    }
}
