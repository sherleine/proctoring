using Proctoring.Application.Sessions;
using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;

namespace Proctoring.Application.Events;

/// <summary>Read-only use cases for AI observations and the voice-sample status of a session.</summary>
public sealed class ObservationQueryService(
    SessionService sessions,
    IObservationRepository observations,
    IVoiceprintRepository voiceprints)
{
    public const int ListLimit = 1000;

    public async Task<IReadOnlyList<AudioObservation>> ListAsync(string sessionId, string token, CancellationToken ct)
    {
        var session = await sessions.AuthorizeAsync(sessionId, token, ct);
        return await observations.ListAsync(session.Id, ListLimit, ct);
    }

    /// <summary>The voiceprint row (without exposing its embedding to callers), or null if no sample was processed.</summary>
    public async Task<SessionVoiceprint?> EnrolmentAsync(string sessionId, string token, CancellationToken ct)
    {
        var session = await sessions.AuthorizeAsync(sessionId, token, ct);
        return await voiceprints.FindAsync(session.Id, ct);
    }
}
