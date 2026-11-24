using System.ComponentModel.DataAnnotations;
using Proctoring.Domain.Events;

namespace Proctoring.Application.Events;

/// <summary>Reads the per-session change log that drives Server-Sent Events.</summary>
public interface IEventUpdateRepository
{
    /// <summary>Updates of a session with Id greater than <paramref name="afterId"/>, oldest first.</summary>
    Task<IReadOnlyList<EventUpdate>> GetAfterAsync(string sessionId, long afterId, int max, CancellationToken ct);

    /// <summary>Highest update id of the session (0 when none).</summary>
    Task<long> GetLatestIdAsync(string sessionId, CancellationToken ct);

    /// <summary>Lowest update id still kept (0 when the log is empty). Older ids were pruned.</summary>
    Task<long> GetOldestRetainedIdAsync(CancellationToken ct);

    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct);

    /// <summary>Current state of the given events of a session (read-only; missing ids are left out).</summary>
    Task<IReadOnlyDictionary<Guid, AudioEvent>> GetEventsAsync(string sessionId, IReadOnlyCollection<Guid> eventIds, CancellationToken ct);
}

/// <summary>
/// "Something changed in session X" signal. Only a hint to wake waiting SSE streams early: streams also poll
/// the database, so a lost signal only delays an update by one poll interval. Never carries data.
/// </summary>
public interface IEventUpdateNotifier
{
    void Notify(string sessionId);

    /// <summary>Completes when <paramref name="sessionId"/> is notified or the timeout elapses.</summary>
    Task WaitAsync(string sessionId, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Live update settings (environment: SSE_*, EVENT_UPDATES_*).</summary>
public sealed class LiveUpdateOptions
{
    /// <summary>SSE_POLL_INTERVAL_MS: how often an idle stream checks the database.</summary>
    [Range(100, 60_000)] public int PollIntervalMs { get; set; } = 1000;

    /// <summary>SSE_HEARTBEAT_SECONDS: comment line sent to keep proxies from closing idle streams.</summary>
    [Range(1, 300)] public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>SSE_REAUTHORIZE_SECONDS: how often an open stream re-checks the session token.</summary>
    [Range(5, 3600)] public int ReauthorizeSeconds { get; set; } = 60;

    /// <summary>EVENT_UPDATES_RETENTION_HOURS: change-log entries older than this are pruned.</summary>
    [Range(1, 720)] public int RetentionHours { get; set; } = 24;

    [Range(1, 1000)] public int BatchSize { get; set; } = 100;
}
