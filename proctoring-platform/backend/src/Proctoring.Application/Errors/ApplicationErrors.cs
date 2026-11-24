namespace Proctoring.Application.Errors;

/// <summary>Base class for errors the API turns into problem+json responses.</summary>
public abstract class AppException(string message) : Exception(message);

/// <summary>404. Used for unknown sessions AND for a well-formed token that does not belong to the session,
/// so a caller cannot find out which session IDs exist.</summary>
public sealed class SessionNotFoundException() : AppException("Session not found");

/// <summary>401. Authorization header missing, not "Bearer", or the token is not in the token format.
/// Decided before any database lookup, so it reveals nothing about sessions.</summary>
public sealed class InvalidCredentialsException(string detail) : AppException(detail);

/// <summary>401. The token belongs to the session but the session ended longer ago than the token TTL.
/// Only reachable with the correct token, so it reveals nothing to anyone else.</summary>
public sealed class TokenExpiredException() : AppException("The session token has expired");

/// <summary>409. An Idempotency-Key was reused with a different request body.</summary>
public sealed class IdempotencyConflictException()
    : AppException("This Idempotency-Key was already used for a different request");

/// <summary>409. The key matches an earlier create, but that session can no longer be returned
/// (replay window passed, session ended, or the server secret changed). Use a new key.</summary>
public sealed class IdempotentReplayUnavailableException(string reason)
    : AppException($"This Idempotency-Key can no longer be replayed ({reason}). Use a new key to create a new session.");
