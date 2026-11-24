using System.Text.Json.Nodes;

namespace Proctoring.Application.Validation;

/// <summary>Validates JSON documents against the versioned contract schemas in docs/contracts/schemas.</summary>
public interface IContractValidator
{
    /// <summary>Errors keyed by JSON path (e.g. "client.userAgent"); empty when valid.</summary>
    IReadOnlyDictionary<string, string[]> Validate(string schemaFileName, JsonNode? document);
}

public static class ContractSchemas
{
    public const string SessionCreateRequest = "session-create.request.v1.json";
    public const string Session = "session.v1.json";
}
