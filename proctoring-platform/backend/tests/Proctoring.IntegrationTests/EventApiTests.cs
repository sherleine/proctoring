using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Proctoring.Application.Events;
using Proctoring.Infrastructure.Validation;
using Proctoring.Tests.Shared;

namespace Proctoring.IntegrationTests;

/// <summary>HTTP tests of the audio event endpoints (multipart upload, retrieval, errors).</summary>
public sealed class EventApiTests : IDisposable
{
    private static readonly JsonSchemaContractValidator Schemas = new(Path.Combine(AppContext.BaseDirectory, "contracts", "schemas"));
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public EventApiTests() : this(new ApiFactory(settings: new() { ["RECORDING_MAX_UPLOAD_BYTES"] = "400000" })) { }

    private EventApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _factory.DeleteDatabase();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private async Task<(string Id, string Token)> NewSessionAsync(HttpClient? client = null)
    {
        var response = await (client ?? _client).PostAsync("/api/v1/sessions",
            new StringContent("""{ "candidateId": "cand-55", "examId": "exam-7" }""", Encoding.UTF8, "application/json"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return ((string)body["sessionId"]!, (string)body["sessionToken"]!);
    }

    private static MultipartFormDataContent Upload(JsonNode? metadata, byte[]? audio, string audioType = "audio/wav", string metadataType = "application/json")
    {
        var form = new MultipartFormDataContent();
        if (metadata is not null)
        {
            var meta = new StringContent(metadata.ToJsonString(), Encoding.UTF8);
            meta.Headers.ContentType = new MediaTypeHeaderValue(metadataType);
            form.Add(meta, "metadata");
        }
        if (audio is not null)
        {
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue(audioType);
            form.Add(file, "audio", "../../etc/passwd.wav"); // client file names are ignored
        }
        return form;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, HttpContent? content = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await (client ?? _client).SendAsync(request);
    }

    private Task<HttpResponseMessage> PostEventAsync(string sessionId, string token, JsonNode? metadata, byte[]? audio, HttpClient? client = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/events", token, Upload(metadata, audio), client);

    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    private static async Task<JsonNode> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"expected {status}, got {response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonNode.Parse(text)!;
        Assert.Equal($"https://proctoring.local/errors/{type}", (string?)body["type"]);
        return body;
    }

    private static void AssertEventSchema(JsonNode body)
    {
        var errors = Schemas.Validate("event-status.v1.json", body);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
    }

    private string[] StoredFiles() => Directory.Exists(_factory.AudioStorePath)
        ? Directory.GetFiles(_factory.AudioStorePath, "*.wav", SearchOption.AllDirectories) : [];

    private string[] StagingFiles() => Directory.Exists(Path.Combine(_factory.AudioStorePath, ".staging"))
        ? Directory.GetFiles(Path.Combine(_factory.AudioStorePath, ".staging")) : [];

    // ------------------------------------------------------------------ happy path

    [Fact]
    public async Task Valid_upload_returns_202_QUEUED_and_the_event_can_be_read_back()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(2);
        var id = Guid.NewGuid();
        var response = await PostEventAsync(sid, token, TestAudio.Metadata(wav, id, Now), wav);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        AssertEventSchema(body);
        Assert.Equal("QUEUED", (string?)body["status"]);
        Assert.Equal(id.ToString(), (string?)body["eventId"]);
        Assert.Equal(2.0, (double)body["durationSeconds"]!, 3);
        Assert.Null(body["result"]);
        Assert.Equal(TestAudio.Sha256(wav), (string?)body["audio"]!["sha256"]);
        Assert.True((bool)body["audio"]!["retained"]!);

        // No internal storage details leak.
        var text = body.ToJsonString();
        foreach (var secret in new[] { _factory.AudioStorePath.Replace("\\", "\\\\"), "storage", ".wav", "file:", "passwd" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);

        // Stored on disk under a server-generated name, byte for byte.
        var stored = Assert.Single(StoredFiles());
        Assert.Equal(wav, await File.ReadAllBytesAsync(stored));
        Assert.Matches("[a-f0-9]{32}\\.wav$", stored);
        Assert.StartsWith(Path.Combine(_factory.AudioStorePath, sid), stored);
        Assert.Empty(StagingFiles());

        // The placeholder queue holds one message for it.
        var queued = Assert.Single(Directory.GetFiles(_factory.QueueDirectory, "*.json"));
        Assert.Contains(id.ToString("N"), queued);

        var get = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events/{id}", token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertEventSchema(await Json(get));

        var list = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events", token));
        var item = Assert.Single(list["items"]!.AsArray());
        AssertEventSchema(item!);

        var result = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events/{id}/result", token);
        var problem = await AssertProblemAsync(result, HttpStatusCode.NotFound, "not-found");
        Assert.Contains("No analysis result", (string?)problem["detail"]);
    }

    [Fact]
    public async Task Events_are_listed_newest_first()
    {
        var (sid, token) = await NewSessionAsync();
        for (var i = 0; i < 3; i++)
        {
            var wav = TestAudio.Wav(0.5, frequency: 300 + i * 100);
            await PostEventAsync(sid, token, TestAudio.Metadata(wav, Guid.NewGuid(), Now.AddSeconds(i), sequence: i), wav);
        }
        var list = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events", token));
        Assert.Equal(new[] { 2, 1, 0 }, list["items"]!.AsArray().Select(x => (int)x!["sequenceNumber"]!));
    }

    [Fact]
    public async Task Events_persist_across_an_application_restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"proctoring-ev-restart-{Guid.NewGuid():N}.db");
        string sid, token;
        var id = Guid.NewGuid();
        using (var first = new ApiFactory(dbPath))
        using (var client = first.CreateClient())
        {
            (sid, token) = await NewSessionAsync(client);
            var wav = TestAudio.Wav(1);
            Assert.Equal(HttpStatusCode.Accepted, (await PostEventAsync(sid, token, TestAudio.Metadata(wav, id, first.Clock.GetUtcNow().UtcDateTime), wav, client)).StatusCode);
        }
        using (var second = new ApiFactory(dbPath))
        using (var client = second.CreateClient())
        {
            var get = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events/{id}", token, client: client);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("QUEUED", (string?)(await Json(get))["status"]);
            Assert.Single(Directory.GetFiles(second.AudioStorePath, "*.wav", SearchOption.AllDirectories));
            second.DeleteDatabase();
        }
    }

    // ------------------------------------------------------------------ audio validation

    public static TheoryData<string, byte[], string> BadAudio() => new()
    {
        { "48 kHz", TestAudio.Wav(1, sampleRate: 48000), "sample rate 48000" },
        { "stereo", TestAudio.Wav(1, channels: 2), "channel count 2" },
        { "8-bit", TestAudio.Wav(1, bitsPerSample: 8), "bit depth 8" },
        { "float encoding", TestAudio.Wav(1, bitsPerSample: 32, formatTag: 3), "format tag 3" },
        { "corrupted", TestAudio.Wav(1)[..5000], "Corrupted WAV" },
        { "not a wav", Encoding.UTF8.GetBytes(new string('x', 2000)), "Not a WAV file" },
        { "no samples", TestAudio.Wav(0), "no samples" },
        { "empty file", [], "empty" },
    };

    [Theory]
    [MemberData(nameof(BadAudio))]
    public async Task Invalid_audio_is_rejected_with_422_and_nothing_is_stored(string name, byte[] audio, string expected)
    {
        var (sid, token) = await NewSessionAsync();
        // Metadata describes the bytes honestly, so only the audio check can fail.
        var meta = TestAudio.Metadata(TestAudio.Wav(1), Guid.NewGuid(), Now);
        meta["audio"]!["sizeBytes"] = Math.Max(44, audio.Length);
        meta["audio"]!["sha256"] = TestAudio.Sha256(audio);

        var response = await PostEventAsync(sid, token, meta, audio);
        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation");
        Assert.Contains(expected, (string?)problem["errors"]!["audio"]![0], StringComparison.OrdinalIgnoreCase);
        Assert.True(StoredFiles().Length == 0, name);
        Assert.Empty(StagingFiles());
    }

    [Fact]
    public async Task Oversized_audio_is_rejected_with_413_while_streaming()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(14); // ~448 KB > RECORDING_MAX_UPLOAD_BYTES (400 KB)
        var response = await PostEventAsync(sid, token, TestAudio.Metadata(wav, Guid.NewGuid(), Now), wav);
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
        Assert.Empty(StoredFiles());
        Assert.Empty(StagingFiles());
    }

    [Fact]
    public async Task A_wav_content_type_does_not_make_other_content_acceptable_and_vice_versa()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var meta = TestAudio.Metadata(wav, Guid.NewGuid(), Now);
        var wrongType = await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sid}/events", token, Upload(meta, wav, audioType: "audio/mpeg"));
        await AssertProblemAsync(wrongType, HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");
    }

    // ------------------------------------------------------------------ request validation

    [Fact]
    public async Task Metadata_that_violates_the_schema_is_rejected_with_field_paths()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var meta = TestAudio.Metadata(wav, Guid.NewGuid(), Now);
        meta["eventId"] = "not-a-uuid";
        meta["audio"]!["sampleRate"] = 48000;
        meta.Remove("trigger");
        meta["candidateId"] = "someone-else"; // not allowed: identity comes from the session

        var problem = await AssertProblemAsync(await PostEventAsync(sid, token, meta, wav), HttpStatusCode.UnprocessableEntity, "validation");
        var keys = problem["errors"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("eventId", keys);
        Assert.Contains("audio.sampleRate", keys);
        Assert.Contains("$", keys); // missing "trigger" / unknown "candidateId"
    }

    [Fact]
    public async Task Missing_parts_wrong_request_type_and_bad_json_are_rejected()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var url = $"/api/v1/sessions/{sid}/events";

        var noAudio = await AssertProblemAsync(await PostEventAsync(sid, token, TestAudio.Metadata(wav, Guid.NewGuid(), Now), null),
            HttpStatusCode.UnprocessableEntity, "validation");
        Assert.NotNull(noAudio["errors"]!["audio"]);
        var noMeta = await AssertProblemAsync(await PostEventAsync(sid, token, null, wav), HttpStatusCode.UnprocessableEntity, "validation");
        Assert.NotNull(noMeta["errors"]!["metadata"]);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, url, token, new ByteArrayContent(wav) { Headers = { ContentType = new("audio/wav") } }),
            HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");

        var badJson = new MultipartFormDataContent { { new StringContent("{ nope", Encoding.UTF8, "application/json"), "metadata" }, { new ByteArrayContent(wav) { Headers = { ContentType = new("audio/wav") } }, "audio", "a.wav" } };
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, url, token, badJson), HttpStatusCode.BadRequest, "bad-request");

        var withVideo = Upload(TestAudio.Metadata(wav, Guid.NewGuid(), Now), wav);
        withVideo.Add(new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("video/webm") } }, "video", "v.webm");
        var video = await AssertProblemAsync(await SendAsync(HttpMethod.Post, url, token, withVideo), HttpStatusCode.UnprocessableEntity, "validation");
        Assert.NotNull(video["errors"]!["video"]);
        Assert.Empty(StoredFiles());
        Assert.Empty(StagingFiles());
    }

    // ------------------------------------------------------------------ authentication and authorization

    [Fact]
    public async Task Uploads_need_a_valid_token_for_that_session()
    {
        var (sid, token) = await NewSessionAsync();
        var (otherSid, otherToken) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        HttpContent Body() => Upload(TestAudio.Metadata(wav, Guid.NewGuid(), Now), wav);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sid}/events", null, Body()), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sid}/events", "garbage", Body()), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sid}/events", "tok_" + new string('A', 43), Body()), HttpStatusCode.NotFound, "not-found");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sid}/events", otherToken, Body()), HttpStatusCode.NotFound, "not-found");
        Assert.Empty(StoredFiles());
        Assert.Empty(StagingFiles());
    }

    [Fact]
    public async Task One_session_cannot_read_another_sessions_events()
    {
        var (sid, token) = await NewSessionAsync();
        var (otherSid, otherToken) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        await PostEventAsync(sid, token, TestAudio.Metadata(wav, id, Now), wav);

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{otherSid}/events/{id}", otherToken), HttpStatusCode.NotFound, "not-found");
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events/{id}", otherToken), HttpStatusCode.NotFound, "not-found");
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events", otherToken), HttpStatusCode.NotFound, "not-found");
        var otherList = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{otherSid}/events", otherToken));
        Assert.Empty(otherList["items"]!.AsArray());
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events/not-a-guid", token), HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Expired_sessions_refuse_uploads_and_expired_tokens_are_rejected()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var start = Now;
        _factory.Clock.Advance(TimeSpan.FromHours(6.5)); // past SESSION_MAX_DURATION_HOURS (6) + grace
        var ended = await AssertProblemAsync(await PostEventAsync(sid, token, TestAudio.Metadata(wav, Guid.NewGuid(), start), wav),
            HttpStatusCode.Conflict, "session-ended");
        Assert.Contains("expired", (string?)ended["detail"]);

        _factory.Clock.Advance(TimeSpan.FromHours(25)); // past the token TTL as well
        await AssertProblemAsync(await PostEventAsync(sid, token, TestAudio.Metadata(wav, Guid.NewGuid(), start), wav),
            HttpStatusCode.Unauthorized, "token-expired");
        Assert.Empty(StoredFiles());
    }

    // ------------------------------------------------------------------ duplicates

    [Fact]
    public async Task Duplicate_and_conflicting_duplicate_uploads()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();
        var meta = TestAudio.Metadata(wav, id, Now);

        Assert.Equal(HttpStatusCode.Accepted, (await PostEventAsync(sid, token, meta, wav)).StatusCode);
        var again = await PostEventAsync(sid, token, meta, wav);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        AssertEventSchema(await Json(again));

        var different = TestAudio.Wav(1, frequency: 1000);
        await AssertProblemAsync(await PostEventAsync(sid, token, TestAudio.Metadata(different, id, Now), different), HttpStatusCode.Conflict, "conflict");

        Assert.Single(StoredFiles());
        Assert.Single(Directory.GetFiles(_factory.QueueDirectory, "*.json"));
    }

    [Fact]
    public async Task Simultaneous_duplicate_uploads_store_and_queue_the_event_once()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(1.5);
        var meta = TestAudio.Metadata(wav, Guid.NewGuid(), Now);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostEventAsync(sid, token, meta.DeepClone(), wav)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Single(StoredFiles());
        Assert.Empty(StagingFiles());
        Assert.Single(Directory.GetFiles(_factory.QueueDirectory, "*.json"));
        var list = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events", token));
        Assert.Single(list["items"]!.AsArray());
    }

    // ------------------------------------------------------------------ failures

    private sealed class DownQueue : IAudioEventQueue
    {
        public volatile bool Down = true;
        public int Published;
        public string Name => "down";

        public Task PublishAsync(AudioEventSubmittedMessage message, CancellationToken ct)
        {
            if (Down) throw new QueueUnavailableException("broker unreachable");
            Interlocked.Increment(ref Published);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Queue_outage_returns_503_keeps_the_event_and_maintenance_recovers_it()
    {
        var queue = new DownQueue();
        using var factory = new ApiFactory { ConfigureServices = s => s.AddSingleton<IAudioEventQueue>(queue) };
        using var client = factory.CreateClient();
        var (sid, token) = await NewSessionAsync(client);
        var wav = TestAudio.Wav(1);
        var id = Guid.NewGuid();

        var response = await PostEventAsync(sid, token, TestAudio.Metadata(wav, id, factory.Clock.GetUtcNow().UtcDateTime), wav, client);
        var problem = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "service-unavailable");
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());
        Assert.Equal("RECEIVED", (string?)problem["event"]!["status"]);
        Assert.Single(Directory.GetFiles(factory.AudioStorePath, "*.wav", SearchOption.AllDirectories));

        queue.Down = false;
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<EventMaintenance>().RunOnceAsync(default)).Republished);

        var get = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events/{id}", token, client: client));
        Assert.Equal("QUEUED", (string?)get["status"]);
        Assert.Equal(1, queue.Published);
        factory.DeleteDatabase();
    }

    /// <summary>Request body that sends part of a multipart upload and then fails like a dropped connection.</summary>
    private sealed class InterruptedContent(byte[] prefix) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(prefix);
            await stream.FlushAsync();
            throw new IOException("client connection lost");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task An_interrupted_upload_leaves_no_files_and_no_event()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(5);
        var full = Upload(TestAudio.Metadata(wav, Guid.NewGuid(), Now), wav);
        var bytes = await full.ReadAsByteArrayAsync();
        var content = new InterruptedContent(bytes[..(bytes.Length / 2)]);
        content.Headers.ContentType = full.Headers.ContentType;

        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sid}/events", token, content));
        await Task.Delay(300); // let the server finish cleaning up the aborted request

        Assert.Empty(StoredFiles());
        Assert.Empty(StagingFiles());
        var list = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{sid}/events", token));
        Assert.Empty(list["items"]!.AsArray());
    }

    [Fact]
    public async Task Audio_and_tokens_never_appear_in_logs()
    {
        var (sid, token) = await NewSessionAsync();
        var wav = TestAudio.Wav(1);
        var meta = TestAudio.Metadata(wav, Guid.NewGuid(), Now);
        await PostEventAsync(sid, token, meta, wav);
        var logs = string.Join("\n", _factory.Logs.Entries);
        Assert.Contains("received for session", logs);
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain(token[4..], logs);
        Assert.DoesNotContain((string)meta["audio"]!["sha256"]!, logs); // no content-derived data either
        Assert.DoesNotContain("RIFF", logs);
        // Request logs never mention storage locations (only the one-time startup configuration line does).
        var requestLogs = string.Join("\n", _factory.Logs.Entries.Where(e => !e.Contains("Audio store:") && !e.Contains("development placeholder")));
        Assert.DoesNotContain(_factory.AudioStorePath, requestLogs);
    }
}
