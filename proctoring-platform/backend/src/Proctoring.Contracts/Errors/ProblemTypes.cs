namespace Proctoring.Contracts.Errors;

/// <summary>RFC 7807 problem "type" URIs used by every error response (docs/contracts/http-api.v1.md).</summary>
public static class ProblemTypes
{
    private const string Base = "https://proctoring.local/errors/";

    public const string Validation = Base + "validation";
    public const string BadRequest = Base + "bad-request";
    public const string Unauthorized = Base + "unauthorized";
    public const string TokenExpired = Base + "token-expired";
    public const string NotFound = Base + "not-found";
    public const string Conflict = Base + "conflict";
    public const string UnsupportedMediaType = Base + "unsupported-media-type";
    public const string PayloadTooLarge = Base + "payload-too-large";
    public const string Internal = Base + "internal";
    public const string SessionEnded = Base + "session-ended";
    public const string ServiceUnavailable = Base + "service-unavailable";
}

/// <summary>Custom headers used by the API.</summary>
public static class ApiHeaders
{
    public const string CorrelationId = "X-Correlation-ID";
    public const string IdempotencyKey = "Idempotency-Key";
    public const string IdempotentReplayed = "Idempotent-Replayed";
}
