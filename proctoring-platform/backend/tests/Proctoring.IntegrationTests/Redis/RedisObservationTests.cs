using System.Net;
using System.Text.Json.Nodes;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;
using StackExchange.Redis;

namespace Proctoring.IntegrationTests.Redis;

/// <summary>
/// Contract 1.1 end to end over HTTP + a real Redis: voice sample -> AI result -> voiceprint -> reference in the next
/// event's stream message -> observations stored and readable; the embedding never reaches an HTTP client.
/// </summary>
[Collection("redis")]
public sealed class RedisObservationTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Schemas = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private static readonly JsonArray Embedding = new(Enumerable.Range(0, 64).Select(i => (JsonNode)Math.Round(Math.Cos(i), 4)).ToArray());
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

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

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
    public async Task Enrolment_then_speech_event_flows_through_redis_and_observations_are_queryable()
    {
        var (sid, token) = await _api.NewSessionAsync();

        // 1. voice sample (15 s) -> AI accepts it
        var enrolWav = TestAudio.Wav(12); // the Redis test settings cap uploads at 400 KB (12 s = 384 KB)
        var enrolId = Guid.NewGuid();
        var upload = await _api.UploadAsync(sid, token, enrolId, enrolWav,
            metadata: TestAudio.Metadata(enrolWav, enrolId, Now, eventType: "enrolment"));
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        Assert.Null((await MessageForAsync(enrolId))["speakerReference"]);

        var enrolResult = TestMessages.Completed(sid, enrolId);
        enrolResult["schemaVersion"] = "1.1";
        enrolResult["enrolment"] = new JsonObject
        {
            ["status"] = "accepted", ["reason"] = null, ["speechSeconds"] = 14.1, ["embedding"] = Embedding.DeepClone(),
            ["embeddingModel"] = "campplus-voxceleb-en:sha256-test",
        };
        await PublishResultAsync(enrolResult);
        await WaitForStatusAsync(sid, token, enrolId, "COMPLETED");

        var enrolment = await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/enrolment", token);
        Assert.Equal(HttpStatusCode.OK, enrolment.StatusCode);
        var enrolmentText = await enrolment.Content.ReadAsStringAsync();
        var enrolmentBody = JsonNode.Parse(enrolmentText)!;
        Assert.Equal("accepted", (string)enrolmentBody["status"]!);
        Assert.True((bool)enrolmentBody["usedForComparison"]!);
        Assert.DoesNotContain("embedding", enrolmentText, StringComparison.OrdinalIgnoreCase);

        // the event result readable by the browser has no embedding
        var enrolEvent = await _api.GetEventAsync(sid, token, enrolId);
        Assert.Null(enrolEvent["result"]!["enrolment"]!["embedding"]);

        // 2. a speech event now carries the reference on the internal stream (and only there)
        var speechId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await _api.UploadAsync(sid, token, speechId)).StatusCode);
        var message = await MessageForAsync(speechId);
        Assert.Equal(enrolId.ToString(), (string)message["speakerReference"]!["enrolmentEventId"]!);
        Assert.Equal(64, message["speakerReference"]!["embedding"]!.AsArray().Count);
        Assert.Empty(Schemas.Validate("audio-event-message.v1.json", message));

        // 3. AI result with an observation -> stored row, readable via the API
        var result = TestMessages.Completed(sid, speechId);
        result["schemaVersion"] = "1.1";
        var obsId = Guid.NewGuid();
        result["observations"] = new JsonArray(new JsonObject
        {
            ["observationId"] = obsId.ToString(), ["sessionId"] = sid, ["eventId"] = speechId.ToString(),
            ["observationType"] = "NON_CANDIDATE_VOICE", ["startSeconds"] = 0.2, ["endSeconds"] = 1.4, ["confidence"] = 0.77,
            ["confidenceKind"] = "model-score", ["supportingEvidence"] = new JsonObject { ["similarityToCandidate"] = 0.08 },
            ["modelName"] = "CAM++ (3D-Speaker, VoxCeleb)", ["modelVersion"] = "campplus-voxceleb-en:sha256-test",
            ["createdAt"] = "2026-11-21T10:00:05.000Z",
        });
        await PublishResultAsync(result);
        await WaitForStatusAsync(sid, token, speechId, "COMPLETED");

        var list = await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = JsonNode.Parse(await list.Content.ReadAsStringAsync())!;
        var item = Assert.Single(body["items"]!.AsArray())!;
        Assert.Equal(obsId.ToString(), (string)item["observationId"]!);
        Assert.Equal("NON_CANDIDATE_VOICE", (string)item["observationType"]!);
        Assert.Equal(1.2, (double)item["durationSeconds"]!, 3);
        Assert.Equal("model-score", (string)item["confidenceKind"]!);
        Assert.Equal(0.08, (double)item["supportingEvidence"]!["similarityToCandidate"]!, 3);
        Assert.Contains("None of them is a determination of misconduct", (string)body["note"]!);
        Assert.Equal("review", (string)item["category"]!);
    }

    [Fact]
    public async Task Observations_and_enrolment_need_the_session_token()
    {
        var (sid, token) = await _api.NewSessionAsync();
        var (otherSid, otherToken) = await _api.NewSessionAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", null)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", otherToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/observations", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.SendAsync(HttpMethod.Get, $"/api/v1/sessions/{otherSid}/enrolment", otherToken)).StatusCode);
    }
}
