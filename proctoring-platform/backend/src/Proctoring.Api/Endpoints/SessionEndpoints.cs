using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Proctoring.Api.Configuration;
using Proctoring.Api.Errors;
using Proctoring.Application.Sessions;
using Proctoring.Application.Validation;
using Proctoring.Contracts.Errors;
using Proctoring.Contracts.Sessions;

namespace Proctoring.Api.Endpoints;

/// <summary>Session routes from docs/contracts/http-api.v1.md.</summary>
public static partial class SessionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^ses_[A-Za-z0-9]{20,}$")]
    private static partial Regex SessionIdPattern();

    /// <summary>
    /// The key can recover the session token during the replay window, so it must be unguessable:
    /// at least 32 characters (e.g. a UUID v4, or 32 random bytes in base64url).
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9._:-]{32,128}$")]
    private static partial Regex IdempotencyKeyPattern();

    public static RouteGroupBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sessions").WithTags("Sessions");

        group.MapPost("", CreateAsync)
            .WithName("CreateSession")
            .WithSummary("Start a monitoring session; returns the session token once")
            .Produces<SessionResponse>(StatusCodes.Status201Created)
            .Produces<SessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{sessionId}", GetAsync)
            .WithName("GetSession")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{sessionId}/end", EndAsync)
            .WithName("EndSession")
            .WithSummary("End the session (idempotent)")
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        SessionService sessions,
        IContractValidator validator,
        IOptions<RecordingPolicyOptions> policy,
        BackendSettings settings,
        CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType())
            return Problems.UnsupportedMediaType(http, "Content-Type must be application/json");

        var idempotencyKey = http.Request.Headers[ApiHeaders.IdempotencyKey].ToString();
        if (idempotencyKey.Length == 0) idempotencyKey = null!;
        else if (!IdempotencyKeyPattern().IsMatch(idempotencyKey))
            return Problems.Validation(http, new Dictionary<string, string[]>
            {
                [ApiHeaders.IdempotencyKey] = ["must be 32-128 characters of letters, digits, '.', '_', ':' or '-' (use a random value such as a UUID v4)"],
            });

        // Read at most MaxJsonBodyBytes whether or not Content-Length was sent (chunked bodies too).
        var raw = await LimitedBody.ReadAsync(http.Request, settings.MaxJsonBodyBytes, ct);
        if (raw is null)
            return Problems.PayloadTooLarge(http, $"Request body must be at most {settings.MaxJsonBodyBytes} bytes");

        // Validate the raw JSON against the contract schema first, then bind it.
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return Problems.BadRequest(http, "Request body is not valid JSON");
        }
        var errors = validator.Validate(ContractSchemas.SessionCreateRequest, body);
        if (errors.Count > 0) return Problems.Validation(http, errors);
        var request = body.Deserialize<CreateSessionRequest>(Json)!;

        var result = await sessions.CreateAsync(request, idempotencyKey, ct);
        var response = SessionMapper.ToResponse(result.Session, policy.Value, result.Token);
        http.Response.Headers.CacheControl = "no-store"; // the response contains a secret
        if (result.Replayed)
        {
            http.Response.Headers[ApiHeaders.IdempotentReplayed] = "true";
            return Results.Ok(response);
        }
        return Results.Created($"/api/v1/sessions/{response.SessionId}", response);
    }

    private static async Task<IResult> GetAsync(string sessionId, HttpContext http, SessionService sessions,
        IOptions<RecordingPolicyOptions> policy, CancellationToken ct)
    {
        var token = BearerToken.Read(http);
        if (!SessionIdPattern().IsMatch(sessionId)) return Problems.NotFound(http);
        var session = await sessions.GetAsync(sessionId, token, ct);
        return Results.Ok(SessionMapper.ToResponse(session, policy.Value));
    }

    private static async Task<IResult> EndAsync(string sessionId, HttpContext http, SessionService sessions,
        IOptions<RecordingPolicyOptions> policy, CancellationToken ct)
    {
        var token = BearerToken.Read(http);
        if (!SessionIdPattern().IsMatch(sessionId)) return Problems.NotFound(http);
        var session = await sessions.EndAsync(sessionId, token, ct);
        return Results.Ok(SessionMapper.ToResponse(session, policy.Value));
    }
}
