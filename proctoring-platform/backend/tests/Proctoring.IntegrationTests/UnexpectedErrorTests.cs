using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Proctoring.Application.Sessions;
using Proctoring.Domain.Sessions;

namespace Proctoring.IntegrationTests;

public sealed class UnexpectedErrorTests
{
    private sealed class BrokenRepository : ISessionRepository
    {
        public Task<Session?> FindAsync(string sessionId, CancellationToken ct) => throw new InvalidOperationException("db exploded: secret-detail");
        public Task<Session?> FindByIdempotencyKeyHashAsync(byte[] hash, CancellationToken ct) => throw new InvalidOperationException("db exploded");
        public void Add(Session session) { }
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Unexpected_errors_are_logged_and_answered_with_a_generic_500()
    {
        using var factory = new ApiFactory { ConfigureServices = s => s.AddScoped<ISessionRepository, BrokenRepository>() };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sessions/ses_01JABCDEFGHJKMNPQRSTVWXYZ");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok_" + new string('A', 43));
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(text)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("https://proctoring.local/errors/internal", (string?)body["type"]);
        Assert.DoesNotContain("secret-detail", text); // no internals in the response
        Assert.Contains(factory.Logs.Entries, e => e.StartsWith("Error: Unhandled error") && e.Contains("secret-detail"));
        factory.DeleteDatabase();
    }
}
