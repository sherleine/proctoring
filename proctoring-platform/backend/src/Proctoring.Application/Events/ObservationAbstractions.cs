using Proctoring.Domain.Events;

namespace Proctoring.Application.Events;

/// <summary>AI observations, one row each (contract 1.1). Shares the unit of work of <see cref="IAudioEventRepository"/>.</summary>
public interface IObservationRepository
{
    Task<bool> ExistsAsync(Guid observationId, CancellationToken ct);
    void Add(AudioObservation observation);

    /// <summary>Oldest first, by absolute time.</summary>
    Task<IReadOnlyList<AudioObservation>> ListAsync(string sessionId, int max, CancellationToken ct);
}
