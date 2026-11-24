namespace Proctoring.Domain.Events;

/// <summary>
/// One entry in the per-session change log ("outbox" for live updates). Written in the same database
/// transaction as the event change it describes, so a change is never visible without its update entry.
/// The auto-incremented <see cref="Id"/> is the Server-Sent Events id: increasing (not contiguous) per session,
/// which is what Last-Event-ID resumption needs.
/// </summary>
public sealed class EventUpdate
{
    public long Id { get; private set; }
    public string SessionId { get; private set; } = default!;
    public Guid EventId { get; private set; }

    /// <summary>Event status after the change (RECEIVED, QUEUED, ...).</summary>
    public AudioEventStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private EventUpdate() { } // EF Core

    public static EventUpdate For(AudioEvent e, DateTime nowUtc) => new()
    {
        SessionId = e.SessionId,
        EventId = e.EventId,
        Status = e.Status,
        CreatedAt = nowUtc,
    };

    public static EventUpdate For(string sessionId, Guid eventId, AudioEventStatus status, DateTime nowUtc) => new()
    {
        SessionId = sessionId,
        EventId = eventId,
        Status = status,
        CreatedAt = nowUtc,
    };
}
