using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Proctoring.Infrastructure.Validation;

namespace Proctoring.IntegrationTests;

/// <summary>HTTP-level tests of the session endpoints, responses checked against the contract schemas.</summary>
public sealed class SessionApiTests : IDisposable
{
    private static readonly string Contracts = Path.Combine(AppContext.BaseDirectory, "contracts");
    private static readonly JsonSchemaContractValidator Schemas = new(Path.Combine(Contracts, "schemas"));
    private static readonly string ExampleRequest = File.ReadAllText(Path.Combine(Contracts, "examples", "session-create.request.v1.json"));

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public SessionApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _factory.DeleteDatabase();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<(JsonNode Body, HttpResponseMessage Response)> CreateAsync(string? body = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sessions") { Content = Json(body ?? ExampleRequest) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        var response = await _client.SendAsync(request);
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync())!, response);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static void AssertMatchesSchema(string schema, JsonNode body)
    {
        var errors = Schemas.Validate(schema, body);
        Assert.True(errors.Count == 0, $"{schema}: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
    }

    private static async Task<JsonNode> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal($"https://proctoring.local/errors/{type}", (string?)body["type"]);
        Assert.Equal((int)status, (int?)body["status"]);
        Assert.False(string.IsNullOrEmpty((string?)body["title"]));
        Assert.False(string.IsNullOrEmpty((string?)body["traceId"]));
        Assert.False(string.IsNullOrEmpty((string?)body["correlationId"]));
        return body;
    }

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Create_with_the_contract_example_returns_201_and_a_schema_valid_session()
    {
        var (body, response) = await CreateAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertMatchesSchema("session.v1.json", body);
        Assert.Equal("active", (string?)body["status"]);
        Assert.Null(body["endedAt"]);
        Assert.Matches("^tok_[A-Za-z0-9_-]{43}$", (string?)body["sessionToken"]);
        Assert.Equal($"/api/v1/sessions/{body["sessionId"]}", response.Headers.Location?.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(10, (double?)body["recordingPolicy"]!["maxEventSeconds"]);
        Assert.Equal("2026-11-21T16:00:00Z", (string?)body["expiresAt"]);
    }

    [Theory]
    [InlineData("""{ "examId": "exam-42" }""")]
    [InlineData("""{ "candidateId": "cand 1", "examId": "exam-42" }""")]
    [InlineData("""{ "candidateId": "cand-1", "examId": "exam-42", "isAdmin": true }""")]
    [InlineData("""[]""")]
    public async Task Create_rejects_requests_that_violate_the_schema(string body)
    {
        var (_, response) = await CreateAsync(body);
        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation");
        Assert.NotEmpty(problem["errors"]!.AsObject());
    }

    [Fact]
    public async Task Create_rejects_malformed_json_and_wrong_content_type()
    {
        var (_, bad) = await CreateAsync("{ not json");
        await AssertProblemAsync(bad, HttpStatusCode.BadRequest, "bad-request");

        var text = await _client.PostAsync("/api/v1/sessions", new StringContent(ExampleRequest, Encoding.UTF8, "text/plain"));
        await AssertProblemAsync(text, HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");
    }

    [Fact]
    public async Task Create_rejects_an_oversized_body()
    {
        var big = $$"""{ "candidateId": "c", "examId": "e", "client": { "userAgent": "{{new string('a', 20_000)}}" } }""";
        var (_, response) = await CreateAsync(big);
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
    }

    // The in-memory test server buffers bodies and adds Content-Length, so a chunked upload cannot be
    // reproduced through HttpClient here; LimitedBody is tested directly with a request without Content-Length.
    [Theory]
    [InlineData(20_000, null, false)] // chunked (no Content-Length), too large -> rejected while reading
    [InlineData(20_000, 100L, false)] // lies about its length -> still stops at the limit
    [InlineData(100, null, true)] // chunked, small -> accepted
    [InlineData(10, 99_999L, false)] // declared too large -> rejected before reading
    public async Task Body_limit_is_enforced_with_or_without_content_length(int actualBytes, long? declared, bool accepted)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[actualBytes]);
        context.Request.ContentLength = declared;
        var result = await Proctoring.Api.Endpoints.LimitedBody.ReadAsync(context.Request, 16 * 1024, default);
        Assert.Equal(accepted, result is not null);
        if (accepted) Assert.Equal(actualBytes, result!.Length);
    }

    // ------------------------------------------------------------------ idempotency

    private static string NewKey() => Guid.NewGuid().ToString(); // UUID v4: 36 characters

    [Fact]
    public async Task Lost_response_retry_returns_the_same_session_and_the_same_token()
    {
        var key = NewKey();
        var (lost, r1) = await CreateAsync(idempotencyKey: key); // pretend this response never arrived
        var (retry, r2) = await CreateAsync(idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("true", r2.Headers.GetValues("Idempotent-Replayed").Single());
        AssertMatchesSchema("session.v1.json", retry);
        Assert.Equal((string?)lost["sessionId"], (string?)retry["sessionId"]);
        Assert.Equal((string?)lost["sessionToken"], (string?)retry["sessionToken"]);
    }

    [Fact]
    public async Task Retry_while_the_token_is_in_use_does_not_invalidate_it()
    {
        var key = NewKey();
        var (first, _) = await CreateAsync(idempotencyKey: key);
        var id = (string)first["sessionId"]!;
        var token = (string)first["sessionToken"]!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{id}", token)).StatusCode);

        await CreateAsync(idempotencyKey: key); // ordinary retry

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{id}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/end", token)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_get_one_session_and_one_token()
    {
        var key = NewKey();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CreateAsync(idempotencyKey: key)));

        Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.Created);
        Assert.All(results, r => Assert.True(r.Response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
        Assert.Single(results.Select(r => (string?)r.Body["sessionId"]).Distinct());
        Assert.Single(results.Select(r => (string?)r.Body["sessionToken"]).Distinct());
    }

    [Fact]
    public async Task Retry_after_the_replay_window_is_a_conflict_and_the_token_keeps_working()
    {
        var key = NewKey();
        var (first, _) = await CreateAsync(idempotencyKey: key);
        _factory.Clock.Advance(TimeSpan.FromMinutes(61));

        var (_, retry) = await CreateAsync(idempotencyKey: key);
        var problem = await AssertProblemAsync(retry, HttpStatusCode.Conflict, "conflict");
        Assert.Contains("replay window", (string?)problem["detail"]);
        var check = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{first["sessionId"]}", (string)first["sessionToken"]!);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_request_is_a_conflict()
    {
        var key = NewKey();
        await CreateAsync(idempotencyKey: key);
        var (_, response) = await CreateAsync("""{ "candidateId": "someone-else", "examId": "exam-42" }""", key);
        await AssertProblemAsync(response, HttpStatusCode.Conflict, "conflict");
    }

    [Theory]
    [InlineData("short-key")] // 9 characters: too guessable for a key that can recover a token
    [InlineData("0123456789012345678901234567890")] // 31 characters
    [InlineData("contains spaces contains spaces contains")]
    public async Task Weak_or_malformed_idempotency_keys_are_rejected(string key)
    {
        var (_, response) = await CreateAsync(idempotencyKey: key);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation");
    }

    // ------------------------------------------------------------------ read + authorization

    [Fact]
    public async Task Get_with_the_token_returns_the_session_without_the_token()
    {
        var (created, _) = await CreateAsync();
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{created["sessionId"]}", (string)created["sessionToken"]!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        AssertMatchesSchema("session.v1.json", body);
        Assert.Null(body["sessionToken"]);
    }

    public static TheoryData<string, string?, HttpStatusCode, string> AuthCases() => new()
    {
        // case name, Authorization header ("{own}" / "{other}" = a real token), expected status, problem type
        { "no header", null, HttpStatusCode.Unauthorized, "unauthorized" },
        { "basic scheme", "Basic dXNlcjpwYXNz", HttpStatusCode.Unauthorized, "unauthorized" },
        { "bearer without token", "Bearer", HttpStatusCode.Unauthorized, "unauthorized" },
        { "bearer with spaces only", "Bearer    ", HttpStatusCode.Unauthorized, "unauthorized" },
        { "malformed token", "Bearer garbage", HttpStatusCode.Unauthorized, "unauthorized" },
        { "token with wrong prefix", "Bearer abc_" + new string('A', 43), HttpStatusCode.Unauthorized, "unauthorized" },
        { "token one char short", "Bearer tok_" + new string('A', 42), HttpStatusCode.Unauthorized, "unauthorized" },
        { "well-formed unknown token", "Bearer tok_" + new string('A', 43), HttpStatusCode.NotFound, "not-found" },
        { "token of another session", "Bearer {other}", HttpStatusCode.NotFound, "not-found" },
        { "own token, lowercase scheme", "bearer {own}", HttpStatusCode.OK, "" },
    };

    [Theory]
    [MemberData(nameof(AuthCases))]
    public async Task Authentication_errors_follow_the_contract(string name, string? header, HttpStatusCode expected, string type)
    {
        var (a, _) = await CreateAsync();
        var (b, _) = await CreateAsync("""{ "candidateId": "cand-b", "examId": "exam-42" }""");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sessions/{a["sessionId"]}");
        if (header is not null)
            request.Headers.TryAddWithoutValidation("Authorization",
                header.Replace("{own}", (string)a["sessionToken"]!).Replace("{other}", (string)b["sessionToken"]!));
        var response = await _client.SendAsync(request);

        Assert.True(expected == response.StatusCode, $"{name}: expected {expected}, got {response.StatusCode}");
        if (type.Length > 0) await AssertProblemAsync(response, expected, type);
        if (expected == HttpStatusCode.Unauthorized) Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task Two_authorization_headers_are_rejected()
    {
        var (a, _) = await CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sessions/{a["sessionId"]}");
        request.Headers.TryAddWithoutValidation("Authorization", new[] { $"Bearer {a["sessionToken"]}", $"Bearer {a["sessionToken"]}" });
        await AssertProblemAsync(await _client.SendAsync(request), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Missing_credentials_are_401_whether_or_not_the_session_exists()
    {
        var (a, _) = await CreateAsync();
        var existing = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{a["sessionId"]}", null);
        var unknown = await SendAsync(HttpMethod.Get, "/api/v1/sessions/ses_01JUNKNOWNUNKNOWNUNKNOWNXX", null);
        var malformedId = await SendAsync(HttpMethod.Post, "/api/v1/sessions/not-a-session/end", null);
        foreach (var r in new[] { existing, unknown, malformedId })
            await AssertProblemAsync(r, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Not_found_responses_do_not_reveal_whether_a_session_exists()
    {
        var (a, _) = await CreateAsync();
        var (b, _) = await CreateAsync("""{ "candidateId": "cand-b", "examId": "exam-42" }""");
        var wrongToken = "tok_" + new string('A', 43);

        var cases = new[]
        {
            await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{a["sessionId"]}", (string)b["sessionToken"]!), // exists, other token
            await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{a["sessionId"]}", wrongToken), // exists, wrong token
            await SendAsync(HttpMethod.Get, "/api/v1/sessions/ses_01JUNKNOWNUNKNOWNUNKNOWNXX", wrongToken), // unknown id
            await SendAsync(HttpMethod.Get, "/api/v1/sessions/not-a-session-id", wrongToken), // malformed id
            await SendAsync(HttpMethod.Post, $"/api/v1/sessions/{a["sessionId"]}/end", (string)b["sessionToken"]!),
        };
        static string Normalize(JsonNode n)
        {
            var copy = n.AsObject().DeepClone().AsObject();
            copy.Remove("traceId");
            copy.Remove("correlationId");
            return copy.ToJsonString();
        }
        var bodies = new List<string>();
        foreach (var r in cases)
        {
            var problem = await AssertProblemAsync(r, HttpStatusCode.NotFound, "not-found");
            Assert.False(r.Headers.Contains("WWW-Authenticate"));
            bodies.Add(Normalize(problem));
        }
        Assert.Single(bodies.Distinct()); // identical apart from per-request IDs

        var after = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{a["sessionId"]}", (string)a["sessionToken"]!);
        Assert.Equal("active", (string?)JsonNode.Parse(await after.Content.ReadAsStringAsync())!["status"]);
    }

    // ------------------------------------------------------------------ end + expiry

    [Fact]
    public async Task End_is_idempotent()
    {
        var (created, _) = await CreateAsync();
        var url = $"/api/v1/sessions/{created["sessionId"]}/end";
        var token = (string)created["sessionToken"]!;

        _factory.Clock.Advance(TimeSpan.FromMinutes(42));
        var first = JsonNode.Parse(await (await SendAsync(HttpMethod.Post, url, token)).Content.ReadAsStringAsync())!;
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var secondResponse = await SendAsync(HttpMethod.Post, url, token);
        var second = JsonNode.Parse(await secondResponse.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        AssertMatchesSchema("session.v1.json", second);
        Assert.Equal("ended", (string?)second["status"]);
        Assert.Equal("candidate", (string?)second["endReason"]);
        Assert.Equal("2026-11-21T10:42:00Z", (string?)first["endedAt"]);
        Assert.Equal((string?)first["endedAt"], (string?)second["endedAt"]);
    }

    [Fact]
    public async Task Session_expires_after_the_maximum_duration()
    {
        var (created, _) = await CreateAsync();
        _factory.Clock.Advance(TimeSpan.FromHours(6.5));
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/sessions/{created["sessionId"]}", (string)created["sessionToken"]!);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertMatchesSchema("session.v1.json", body);
        Assert.Equal("ended", (string?)body["status"]);
        Assert.Equal("expired", (string?)body["endReason"]);
        Assert.Equal((string?)body["expiresAt"], (string?)body["endedAt"]);
    }

    [Fact]
    public async Task Token_stops_working_after_the_ttl_following_the_end()
    {
        var (created, _) = await CreateAsync();
        var url = $"/api/v1/sessions/{created["sessionId"]}";
        var token = (string)created["sessionToken"]!;
        await SendAsync(HttpMethod.Post, $"{url}/end", token);

        _factory.Clock.Advance(TimeSpan.FromHours(25));
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, url, token), HttpStatusCode.Unauthorized, "token-expired");
    }

    // ------------------------------------------------------------------ cross-cutting

    [Fact]
    public async Task Correlation_id_is_echoed_when_valid_and_generated_otherwise()
    {
        using var withId = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sessions/ses_01JUNKNOWNUNKNOWNUNKNOWNXX");
        withId.Headers.Add("X-Correlation-ID", "client-trace.42");
        withId.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok_" + new string('A', 43));
        var r1 = await _client.SendAsync(withId);
        Assert.Equal("client-trace.42", r1.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("client-trace.42", (string?)JsonNode.Parse(await r1.Content.ReadAsStringAsync())!["correlationId"]);

        using var injected = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        injected.Headers.TryAddWithoutValidation("X-Correlation-ID", "bad value\nwith newline");
        var r2 = await _client.SendAsync(injected);
        Assert.Matches("^[a-f0-9]{32}$", r2.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Tokens_never_appear_in_logs()
    {
        var key = NewKey();
        var (created, _) = await CreateAsync(idempotencyKey: key);
        var (replayed, _) = await CreateAsync(idempotencyKey: key);
        var url = $"/api/v1/sessions/{created["sessionId"]}";
        await SendAsync(HttpMethod.Get, url, (string)replayed["sessionToken"]!);
        await SendAsync(HttpMethod.Get, url, "tok_" + new string('B', 43)); // rejected token
        await SendAsync(HttpMethod.Post, $"{url}/end", (string)replayed["sessionToken"]!);

        var logs = string.Join("\n", _factory.Logs.Entries);
        Assert.Contains("HTTP GET /api/v1/sessions/{sessionId} responded 200", logs); // logging works
        foreach (var secret in new[] { (string)created["sessionToken"]!, ((string)created["sessionToken"]!)[4..], key, new string('B', 43) })
            Assert.DoesNotContain(secret, logs);
        Assert.DoesNotContain("Bearer", logs);
    }

    [Fact]
    public async Task Unknown_routes_return_problem_json()
    {
        await AssertProblemAsync(await _client.GetAsync("/api/v1/nope"), HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Health_endpoints_report_status()
    {
        var live = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("healthy", (string?)JsonNode.Parse(await live.Content.ReadAsStringAsync())!["status"]);

        var ready = await _client.GetAsync("/health/ready");
        var body = JsonNode.Parse(await ready.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("healthy", (string?)body["status"]);
        Assert.Contains(body["checks"]!.AsArray(), c => (string?)c!["name"] == "database" && (string?)c["status"] == "healthy");
    }

    [Fact]
    public async Task OpenApi_document_lists_the_session_routes()
    {
        var doc = await _client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/v1/sessions/{sessionId}/end", doc);
    }

    [Fact]
    public async Task Cors_allows_the_configured_origin_only()
    {
        async Task<HttpResponseMessage> Preflight(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/sessions");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,idempotency-key");
            return await _client.SendAsync(request);
        }

        var allowed = await Preflight("http://localhost:5173");
        Assert.Equal("http://localhost:5173", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var denied = await Preflight("https://evil.example");
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
