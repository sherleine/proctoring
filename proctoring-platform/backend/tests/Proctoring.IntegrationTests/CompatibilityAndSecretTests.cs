using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Proctoring.Infrastructure.Validation;

namespace Proctoring.IntegrationTests;

/// <summary>
/// Backward compatibility with the Phase 1 (v1.0) contract, using frozen copies of the v1.0 schemas in
/// docs/contracts/schemas/history.
/// </summary>
public sealed class ContractCompatibilityTests : IDisposable
{
    private static readonly string Contracts = Path.Combine(AppContext.BaseDirectory, "contracts");
    private static readonly JsonSchemaContractValidator Current = new(Path.Combine(Contracts, "schemas"));
    private static readonly JsonSchemaContractValidator V10 = new(Path.Combine(Contracts, "schemas", "history"));
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ContractCompatibilityTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _factory.DeleteDatabase();
    }

    private static JsonNode Example(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Contracts, "examples", name)))!;

    private static string Errors(IReadOnlyDictionary<string, string[]> e) => string.Join("; ", e.Select(x => $"{x.Key}: {string.Join(",", x.Value)}"));

    [Fact]
    public void Phase1_session_examples_are_valid_under_both_v10_and_current_schemas()
    {
        foreach (var (example, v10Schema) in new[]
                 {
                     ("session-create.request.v1.json", "session-create.request.v1.0.json"),
                     ("session.v1.json", "session.v1.0.json"),
                 })
        {
            var doc = Example(example);
            var old = V10.Validate(v10Schema, doc);
            var now = Current.Validate(example, doc);
            Assert.True(old.Count == 0, $"{example} vs v1.0: {Errors(old)}");
            Assert.True(now.Count == 0, $"{example} vs current: {Errors(now)}");
        }
    }

    [Fact]
    public async Task A_v10_client_request_without_idempotency_key_still_works()
    {
        var response = await _client.PostAsync("/api/v1/sessions",
            new StringContent(Example("session-create.request.v1.json").ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Responses_keep_every_v10_field_and_only_add_the_documented_optional_fields()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/sessions", new { candidateId = "cand-1", examId = "exam-1" });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        // Valid under the current schema.
        Assert.True(Current.Validate("session.v1.json", body).Count == 0);

        // Removing the two 1.1 additions gives a document that is valid under the frozen v1.0 schema:
        // every v1.0 field is present with the v1.0 type and meaning.
        var asV10 = body.DeepClone().AsObject();
        asV10.Remove("expiresAt");
        asV10.Remove("endReason");
        var v10Errors = V10.Validate("session.v1.0.json", asV10);
        Assert.True(v10Errors.Count == 0, Errors(v10Errors));

        // Documented compatibility note: the v1.0 schema had additionalProperties:false, so a client that
        // validated responses strictly against v1.0 rejects the new optional fields.
        Assert.Equal(["expiresAt", "endReason"], body.Select(p => p.Key).Except(asV10.Select(p => p.Key)).ToArray());
        Assert.NotEmpty(V10.Validate("session.v1.0.json", body));
    }
}

public sealed class TokenSecretConfigurationTests
{
    [Fact]
    public void Production_requires_a_token_secret()
    {
        using var factory = new ApiFactory(environment: "Production");
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("SESSION_TOKEN_SECRET is required", ex.ToString());
        factory.DeleteDatabase();
    }

    [Theory]
    [InlineData("not base64 !!", "must be base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==", "at least 32 bytes")] // 16 bytes
    public void Invalid_token_secrets_are_rejected_without_echoing_them(string secret, string expected)
    {
        using var factory = new ApiFactory(settings: new() { ["SESSION_TOKEN_SECRET"] = secret }, environment: "Production");
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(expected, ex.ToString());
        Assert.DoesNotContain(secret, ex.ToString());
        factory.DeleteDatabase();
    }

    [Fact]
    public async Task A_configured_secret_keeps_idempotent_replays_working_across_a_restart()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var dbPath = Path.Combine(Path.GetTempPath(), $"proctoring-secret-{Guid.NewGuid():N}.db");
        var settings = new Dictionary<string, string> { ["SESSION_TOKEN_SECRET"] = secret };
        var key = Guid.NewGuid().ToString();

        async Task<JsonNode> Create(ApiFactory f)
        {
            using var client = f.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sessions")
            {
                Content = new StringContent("""{ "candidateId": "cand-1", "examId": "exam-1" }""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", key);
            var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }

        JsonNode first, second;
        using (var a = new ApiFactory(dbPath, settings, "Production")) first = await Create(a);
        using (var b = new ApiFactory(dbPath, settings, "Production")) second = await Create(b);
        Assert.Equal((string?)first["sessionToken"], (string?)second["sessionToken"]);

        // Restart with a different secret: the replay is refused (409) rather than issuing a different token.
        var other = new Dictionary<string, string> { ["SESSION_TOKEN_SECRET"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        using (var c = new ApiFactory(dbPath, other, "Production"))
        using (var client = c.CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sessions")
            {
                Content = new StringContent("""{ "candidateId": "cand-1", "examId": "exam-1" }""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", key);
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
            c.DeleteDatabase();
        }
    }
}
