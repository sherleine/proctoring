using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;

namespace Proctoring.Application.Events;

/// <summary>AI observations, one row each (contract 1.1). Shares the unit of work of <see cref="IAudioEventRepository"/>.</summary>
public interface IObservationRepository
{
    Task<bool> ExistsAsync(Guid observationId, CancellationToken ct);
    void Add(AudioObservation observation);

    /// <summary>Oldest first, by absolute time.</summary>
    Task<IReadOnlyList<AudioObservation>> ListAsync(string sessionId, int max, CancellationToken ct);
}

/// <summary>Per-session candidate voice reference. Shares the unit of work of <see cref="IAudioEventRepository"/>.</summary>
public interface IVoiceprintRepository
{
    Task<SessionVoiceprint?> FindAsync(string sessionId, CancellationToken ct);
    void Add(SessionVoiceprint voiceprint);

    /// <summary>Deletes voiceprints last updated before the cut-off (retention). Returns how many were removed.</summary>
    Task<int> DeleteUpdatedBeforeAsync(DateTime cutoffUtc, CancellationToken ct);
}
