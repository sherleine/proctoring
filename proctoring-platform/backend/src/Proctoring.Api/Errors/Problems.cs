using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Proctoring.Api.Middleware;
using Proctoring.Application.Errors;
using Proctoring.Contracts.Errors;

namespace Proctoring.Api.Errors;

/// <summary>Builds RFC 7807 problem+json responses in the format documented in http-api.v1.md.</summary>
public static class Problems
{
    public static IResult Create(HttpContext context, int status, string type, string title, string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails { Type = type, Title = title, Status = status, Detail = detail };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context);
        if (errors is not null) problem.Extensions["errors"] = errors;
        return Results.Problem(problem);
    }

    public static IResult Validation(HttpContext c, IReadOnlyDictionary<string, string[]> errors) =>
        Create(c, StatusCodes.Status422UnprocessableEntity, ProblemTypes.Validation, "Validation failed",
            string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}")), errors);

    public static IResult BadRequest(HttpContext c, string detail) =>
        Create(c, StatusCodes.Status400BadRequest, ProblemTypes.BadRequest, "Bad request", detail);

    public static IResult UnsupportedMediaType(HttpContext c, string detail) =>
        Create(c, StatusCodes.Status415UnsupportedMediaType, ProblemTypes.UnsupportedMediaType, "Unsupported media type", detail);

    public static IResult PayloadTooLarge(HttpContext c, string detail) =>
        Create(c, StatusCodes.Status413PayloadTooLarge, ProblemTypes.PayloadTooLarge, "Payload too large", detail);

    public static IResult NotFound(HttpContext c) =>
        Create(c, StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Not found", "Session not found");
}

/// <summary>Turns application exceptions into problem responses; anything unexpected becomes a generic 500.</summary>
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        IResult result = exception switch
        {
            SessionNotFoundException => Problems.NotFound(context),
            InvalidCredentialsException e => Unauthorized(context, ProblemTypes.Unauthorized, "Unauthorized", e.Message),
            TokenExpiredException e => Unauthorized(context, ProblemTypes.TokenExpired, "Token expired", e.Message),
            IdempotencyConflictException e => Problems.Create(context, StatusCodes.Status409Conflict, ProblemTypes.Conflict, "Conflict", e.Message),
            IdempotentReplayUnavailableException e => Problems.Create(context, StatusCodes.Status409Conflict, ProblemTypes.Conflict, "Conflict", e.Message),
            RequestValidationException e => Problems.Validation(context, e.Errors),
            InvalidAudioException e => Problems.Validation(context, new Dictionary<string, string[]> { ["audio"] = [e.Message] }),
            AudioTooLargeException e => Problems.PayloadTooLarge(context, e.Message),
            UnsupportedMediaTypeException e => Problems.UnsupportedMediaType(context, e.Message),
            MalformedRequestException e => Problems.BadRequest(context, e.Message),
            SessionNotAcceptingUploadsException e => Problems.Create(context, StatusCodes.Status409Conflict, ProblemTypes.SessionEnded, "Session not accepting uploads", e.Message),
            EventConflictException e => Problems.Create(context, StatusCodes.Status409Conflict, ProblemTypes.Conflict, "Conflict", e.Message),
            EventNotFoundException e => Problems.Create(context, StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Not found", e.Message),
            ResultNotAvailableException e => Problems.Create(context, StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Not found", e.Message),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => Problems.PayloadTooLarge(context, "Request body too large"),
            BadHttpRequestException e => Problems.BadRequest(context, e.Message),
            _ => Internal(context, exception),
        };
        await result.ExecuteAsync(context);
        return true;
    }

    private static IResult Unauthorized(HttpContext context, string type, string title, string detail)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Problems.Create(context, StatusCodes.Status401Unauthorized, type, title, detail);
    }

    private IResult Internal(HttpContext context, Exception exception)
    {
        logger.LogError(exception, "Unhandled error");
        // No exception details in the response: they can contain internals.
        return Problems.Create(context, StatusCodes.Status500InternalServerError, ProblemTypes.Internal, "Internal server error",
            "An unexpected error occurred. Quote the correlationId when reporting it.");
    }
}
