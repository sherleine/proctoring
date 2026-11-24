using System.Text.Json.Nodes;
using Proctoring.Application.Validation;
using Proctoring.Infrastructure.Validation;

namespace Proctoring.UnitTests;

/// <summary>The validator must accept every Phase 1 contract example and reject contract violations.</summary>
public class ContractValidatorTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts");
    private static readonly JsonSchemaContractValidator Validator = new(Path.Combine(Root, "schemas"));

    public static TheoryData<string> Examples() =>
        new(Directory.EnumerateFiles(Path.Combine(Root, "examples"), "*.json").Select(Path.GetFileName).ToArray()!);

    [Theory]
    [MemberData(nameof(Examples))]
    public void Every_contract_example_is_valid(string example)
    {
        // Each example is named after the schema it instantiates.
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", example)));
        var errors = Validator.Validate(example, json);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
    }

    [Fact]
    public void Nested_refs_are_resolved()
    {
        // event-status embeds analysis-result via $ref: an invalid nested finding must be caught.
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", "event-status.v1.json")))!;
        json["result"]!["findings"]![0]!["type"] = "CHEATING_CONFIRMED";
        Assert.NotEmpty(Validator.Validate("event-status.v1.json", json));
    }

    [Theory]
    [InlineData("""{ "examId": "exam-42" }""", "$")] // candidateId missing
    [InlineData("""{ "candidateId": "", "examId": "exam-42" }""", "candidateId")] // empty
    [InlineData("""{ "candidateId": "cand 123", "examId": "exam-42" }""", "candidateId")] // space not allowed
    [InlineData("""{ "candidateId": "cand-1", "examId": "exam-42", "role": "admin" }""", "$")] // unknown property
    [InlineData("""{ "candidateId": "cand-1", "examId": "exam-42", "client": { "userAgent": 5 } }""", "client.userAgent")]
    public void Invalid_create_requests_are_rejected_with_a_path(string body, string expectedKey)
    {
        var errors = Validator.Validate(ContractSchemas.SessionCreateRequest, JsonNode.Parse(body));
        Assert.Contains(expectedKey, errors.Keys);
    }
}
