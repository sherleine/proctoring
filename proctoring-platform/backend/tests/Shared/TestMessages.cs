using System.Text.Json.Nodes;

namespace Proctoring.Tests.Shared;

/// <summary>
/// Processing-service messages built from the published contract examples (docs/contracts/examples), so tests
/// exercise exactly what a real processing service would send.
/// </summary>
public static class TestMessages
{
    private static string Example(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "examples", name));

    public static JsonObject Started(string sessionId, Guid eventId, int deliveryCount = 1)
    {
        var p = JsonNode.Parse(Example("analysis-started.v1.json"))!.AsObject();
        p["sessionId"] = sessionId;
        p["eventId"] = eventId.ToString();
        p["deliveryCount"] = deliveryCount;
        return p;
    }

    public static JsonObject Completed(string sessionId, Guid eventId, string text = "Can you help me with this question?")
    {
        var p = JsonNode.Parse(Example("analysis-result.v1.json"))!.AsObject();
        p["sessionId"] = sessionId;
        p["eventId"] = eventId.ToString();
        p["status"] = "completed";
        p["error"] = null;
        p["transcription"]!["text"] = text;
        return p;
    }

    public static JsonObject Failed(string sessionId, Guid eventId, string code = "ASR_FAILED", bool retryable = false, int deliveryCount = 1)
    {
        var p = Completed(sessionId, eventId);
        p["status"] = "failed";
        p["error"] = new JsonObject { ["code"] = code, ["message"] = $"{code} in test", ["retryable"] = retryable, ["deliveryCount"] = deliveryCount };
        return p;
    }

    public static string TypeOf(JsonObject payload) => payload["status"]?.GetValue<string>() switch
    {
        "completed" => "audio.analysis.completed",
        "failed" => "audio.analysis.failed",
        _ => "audio.analysis.started",
    };

    /// <summary>Flat stream-entry fields (stream-envelope.v1.json).</summary>
    public static Dictionary<string, string> Envelope(JsonObject payload, string? messageType = null) => new()
    {
        ["messageType"] = messageType ?? TypeOf(payload),
        ["schemaVersion"] = "1",
        ["messageId"] = Guid.NewGuid().ToString(),
        ["correlationId"] = (string)payload["eventId"]!,
        ["sessionId"] = (string)payload["sessionId"]!,
        ["eventId"] = (string)payload["eventId"]!,
        ["producedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["payload"] = payload.ToJsonString(),
    };
}
