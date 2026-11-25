using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Proctoring.Api.Errors;
using Proctoring.Application.Events;

namespace Proctoring.Api.Endpoints;

/// <summary>
/// Contract 1.1 read route: the session's AI observations (one row each).
/// Same authentication as the event routes (the session token).
/// </summary>
public static partial class ObservationEndpoints
{
    [GeneratedRegex("^ses_[A-Za-z0-9]{20,}$")]
    private static partial Regex SessionIdPattern();

    public sealed record ObservationResponse(
        string ObservationId, string SessionId, string EventId, string ObservationType, string Category,
        double StartSeconds, double EndSeconds, DateTime StartTimestamp, DateTime EndTimestamp, double DurationSeconds,
        double Confidence, string ConfidenceKind, JsonNode? SupportingEvidence, string ModelName, string ModelVersion,
        DateTime CreatedAt);

    public sealed record ObservationListResponse(IReadOnlyList<ObservationResponse> Items, string Note);

    public static void MapObservationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sessions/{sessionId}").WithTags("Observations");
        group.MapGet("/observations", ListAsync).WithName("ListObservations").Produces<ObservationListResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(string sessionId, HttpContext http, ObservationQueryService service, CancellationToken ct)
    {
        var token = BearerToken.Read(http);
        if (!SessionIdPattern().IsMatch(sessionId)) return Problems.NotFound(http);
        var items = await service.ListAsync(sessionId, token, ct);
        var body = items.Select(o => new ObservationResponse(
            o.Id.ToString(), o.SessionId, o.EventId.ToString(), o.ObservationType, o.Category, o.StartSeconds, o.EndSeconds,
            o.OccurredAt, o.OccurredAt.AddSeconds(o.EndSeconds - o.StartSeconds), Math.Round(o.EndSeconds - o.StartSeconds, 3),
            o.Confidence, o.ConfidenceKind, JsonNode.Parse(o.SupportingEvidenceJson), o.ModelName, o.ModelVersion,
            o.CreatedAt)).ToList();
        return Results.Ok(new ObservationListResponse(body,
            "Observations with category 'review' are potential observations for human review; 'informational' ones only " +
            "record whether speech was present. None of them is a determination of misconduct."));
    }
}
