using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proctoring.Domain.Events;

namespace Proctoring.Application.Events;

public sealed record MaintenanceReport(int Republished, int MarkedMissing, int StagingFilesDeleted, int AudioFilesExpired);

/// <summary>
/// Periodic housekeeping that makes uploads safe against crashes and outages:
///  - re-publishes RECEIVED events whose publish failed (queue down) or was interrupted;
///  - marks RECEIVED events whose audio never reached storage as FAILED (AUDIO_FILE_MISSING);
///  - deletes staged upload files left behind by aborted requests or crashes;
///  - deletes stored audio after AUDIO_RETENTION_HOURS (the event record is kept).
/// Every step is idempotent, so running it twice (or on two instances) is harmless.
/// </summary>
public sealed class EventMaintenance(
    IAudioEventRepository events,
    IAudioStore store,
    EventPublisher publisher,
    TimeProvider clock,
    IOptions<AudioEventOptions> options,
    ILogger<EventMaintenance> logger)
{
    private const int Batch = 100;
    private readonly AudioEventOptions _options = options.Value;

    public async Task<MaintenanceReport> RunOnceAsync(CancellationToken ct)
    {
        var (republished, missing) = await RepublishPendingAsync(ct);
        var staging = await store.DeleteStaleStagingAsync(TimeSpan.FromMinutes(_options.StagingMaxAgeMinutes), ct);
        var expired = await ApplyRetentionAsync(ct);
        var report = new MaintenanceReport(republished, missing, staging, expired);
        if (report != new MaintenanceReport(0, 0, 0, 0))
            logger.LogInformation("Event maintenance: {Republished} re-published, {Missing} marked missing, {Staging} staging files and {Expired} expired audio files deleted",
                republished, missing, staging, expired);
        return report;
    }

    public async Task<(int Republished, int MarkedMissing)> RepublishPendingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await events.FindUnpublishedAsync(now.AddSeconds(-_options.RepublishAfterSeconds), now, Batch, ct);
        int republished = 0, missing = 0;
        foreach (var e in pending)
        {
            PublishOutcome outcome;
            try
            {
                outcome = await publisher.PublishAsync(e, ct);
            }
            catch (QueueUnavailableException ex)
            {
                logger.LogWarning("Queue still unavailable; {Count} events wait for re-publishing: {Reason}", pending.Count - republished, ex.Message);
                break;
            }
            if (outcome == PublishOutcome.Queued) republished++;
            else if (outcome == PublishOutcome.AudioMissing && e.ReceivedAt < now.AddMinutes(-_options.MissingAudioAfterMinutes))
            {
                // The upload crashed between the database write and the file write and was never retried.
                // This is a technical failure, not an observation about the candidate.
                e.MarkFailed("AUDIO_FILE_MISSING", "The audio file was not stored (interrupted upload)", now);
                await events.SaveChangesAsync(ct);
                missing++;
            }
        }
        return (republished, missing);
    }

    public async Task<int> ApplyRetentionAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await events.FindWithAudioReceivedBeforeAsync(now - _options.AudioRetention, Batch, ct);
        foreach (var e in due)
        {
            await store.DeleteAsync(e.StorageKey!, ct);
            e.AudioRemoved(now);
            if (e.Status is AudioEventStatus.Received or AudioEventStatus.Queued)
                e.MarkFailed("AUDIO_RETENTION_EXPIRED", "The audio was deleted by the retention policy before it was analysed", now);
            await events.SaveChangesAsync(ct);
        }
        return due.Count;
    }
}
