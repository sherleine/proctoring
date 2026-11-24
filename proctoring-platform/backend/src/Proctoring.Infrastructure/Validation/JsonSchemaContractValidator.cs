using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Proctoring.Application.Validation;

namespace Proctoring.Infrastructure.Validation;

/// <summary>
/// Validates documents against the JSON Schemas in docs/contracts/schemas (copied next to the
/// binaries at build time). The schemas are the contract, so the API enforces exactly what is documented.
/// </summary>
public sealed class JsonSchemaContractValidator : IContractValidator
{
    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    private static readonly EvaluationOptions Evaluation = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true, // "format": "uuid" / "date-time" are enforced, not just annotations
    };

    public JsonSchemaContractValidator(string schemaDirectory)
    {
        if (!Directory.Exists(schemaDirectory))
            throw new DirectoryNotFoundException($"Contract schema directory not found: {schemaDirectory}");

        var build = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        // Build in dependency order is not needed: schemas reference each other by $id, resolved at evaluation.
        foreach (var file in Directory.EnumerateFiles(schemaDirectory, "*.json"))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file), build);
            _schemas[Path.GetFileName(file)] = schema;
        }
        if (_schemas.Count == 0) throw new InvalidOperationException($"No schemas in {schemaDirectory}");
    }

    public IReadOnlyCollection<string> SchemaNames => _schemas.Keys;

    public IReadOnlyDictionary<string, string[]> Validate(string schemaFileName, JsonNode? document)
    {
        if (!_schemas.TryGetValue(schemaFileName, out var schema))
            throw new ArgumentException($"Unknown contract schema {schemaFileName}", nameof(schemaFileName));

        using var json = JsonDocument.Parse(document?.ToJsonString() ?? "null");
        var result = schema.Evaluate(json.RootElement, Evaluation);
        if (result.IsValid) return new Dictionary<string, string[]>();

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var detail in result.Details ?? [])
        {
            if (detail.Errors is null || detail.IsValid) continue;
            var key = ToPath(detail.InstanceLocation.ToString());
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = [];
            foreach (var message in detail.Errors.Values)
                if (!list.Contains(message)) list.Add(message);
        }
        if (errors.Count == 0) errors["$"] = ["Document does not match the contract"];
        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    /// <summary>"/client/userAgent" -> "client.userAgent"; root -> "$".</summary>
    private static string ToPath(string pointer) =>
        string.IsNullOrEmpty(pointer) || pointer == "/" ? "$" : pointer.TrimStart('/').Replace('/', '.');
}
