using Proctoring.Application.Sessions;
using Proctoring.Domain.Events;

namespace Proctoring.Application.Events;

/// <summary>Read-only use case: the AI observations of a session.</summary>
public sealed class ObservationQueryService(SessionService sessions, IObservationRepository observations)
{
    public const int ListLimit = 1000;

    public async Task<IReadOnlyList<AudioObservation>> ListAsync(string sessionId, string token, CancellationToken ct)
    {
        var session = await sessions.AuthorizeAsync(sessionId, token, ct);
        return await observations.ListAsync(session.Id, ListLimit, ct);
    }
}
