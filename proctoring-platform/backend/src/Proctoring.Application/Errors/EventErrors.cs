namespace Proctoring.Application.Errors;

/// <summary>422. Request content does not satisfy the contract (schema or cross-field checks).</summary>
public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : AppException("Validation failed")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public static RequestValidationException For(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>422 (errors.audio). The audio is empty, corrupted or in an unsupported format.</summary>
public sealed class InvalidAudioException(string kind, string message) : AppException(message)
{
    public const string Empty = "empty";
    public const string Corrupted = "corrupted";
    public const string Unsupported = "unsupported";

    /// <summary>"empty" | "corrupted" | "unsupported".</summary>
    public string Kind { get; } = kind;
}

/// <summary>413. More bytes arrived than allowed (detected while streaming).</summary>
public sealed class AudioTooLargeException(long maxBytes) : AppException($"The audio must be at most {maxBytes} bytes");

/// <summary>415. Wrong Content-Type for the request or for a part.</summary>
public sealed class UnsupportedMediaTypeException(string message) : AppException(message);

/// <summary>400. The multipart body could not be read.</summary>
public sealed class MalformedRequestException(string message) : AppException(message);

/// <summary>409. The session no longer accepts uploads.</summary>
public sealed class SessionNotAcceptingUploadsException(string message) : AppException(message);

/// <summary>409. Same eventId, different metadata or audio.</summary>
public sealed class EventConflictException()
    : AppException("An event with this eventId already exists in the session with different metadata or audio");

/// <summary>404. Unknown event in this session (or an event of another session - indistinguishable).</summary>
public sealed class EventNotFoundException() : AppException("Event not found");

/// <summary>404. The event exists but has no analysis result yet.</summary>
public sealed class ResultNotAvailableException() : AppException("No analysis result is available for this event yet");
