using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Proctoring.IntegrationTests;

public sealed class StartupAndPersistenceTests
{
    [Fact]
    public async Task Sessions_persist_across_an_application_restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"proctoring-restart-{Guid.NewGuid():N}.db");
        string sessionId, token;
        using (var first = new ApiFactory(dbPath))
        using (var client = first.CreateClient())
        {
            var created = await client.PostAsJsonAsync("/api/v1/sessions", new { candidateId = "cand-1", examId = "exam-1" });
            var body = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
            sessionId = (string)body["sessionId"]!;
            token = (string)body["sessionToken"]!;
        }

        using (var second = new ApiFactory(dbPath)) // migrations run again: must be a no-op
        using (var client = second.CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sessions/{sessionId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The schema in the file is the one created by the migration.
        await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
            Assert.EndsWith("_InitialCreate", (string)(await command.ExecuteScalarAsync())!);
        }
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
    }

    [Theory]
    [InlineData("RECORDING_MAX_EVENT_SECONDS", "100", "RECORDING_*")]
    [InlineData("SESSION_MAX_DURATION_HOURS", "abc", "SESSION_MAX_DURATION_HOURS")]
    [InlineData("RECORDING_SPLIT_OVERLAP_SECONDS", "3", "RECORDING_SPLIT_OVERLAP_SECONDS must be smaller")]
    [InlineData("LOG_FORMAT", "xml", "backend")]
    public void Invalid_configuration_stops_the_application_at_startup(string key, string value, string expectedMessage)
    {
        var overrides = new Dictionary<string, string> { [key] = value };
        if (key == "RECORDING_SPLIT_OVERLAP_SECONDS") overrides["RECORDING_MAX_EVENT_SECONDS"] = "3";
        using var factory = new ApiFactory(settings: overrides);
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(expectedMessage, ex.ToString());
        factory.DeleteDatabase();
    }

    [Fact]
    public async Task Recording_policy_comes_from_configuration()
    {
        using var factory = new ApiFactory(settings: new() { ["RECORDING_MAX_EVENT_SECONDS"] = "8", ["RECORDING_UPLOAD_VIDEO"] = "true" });
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new { candidateId = "cand-1", examId = "exam-1" });
        var policy = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["recordingPolicy"]!;
        Assert.Equal(8, (double?)policy["maxEventSeconds"]);
        Assert.True((bool?)policy["uploadVideo"]);
        factory.DeleteDatabase();
    }
}
