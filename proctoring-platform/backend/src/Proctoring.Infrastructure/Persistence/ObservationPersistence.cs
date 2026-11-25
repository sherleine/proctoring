using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Proctoring.Application.Events;
using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;

namespace Proctoring.Infrastructure.Persistence;

internal sealed class AudioObservationConfiguration : IEntityTypeConfiguration<AudioObservation>
{
    public void Configure(EntityTypeBuilder<AudioObservation> b)
    {
        b.ToTable("observations");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).HasColumnName("id");
        b.Property(o => o.SessionId).HasColumnName("session_id").HasMaxLength(40).IsRequired();
        b.Property(o => o.EventId).HasColumnName("event_id").IsRequired();
        b.Property(o => o.ObservationType).HasColumnName("observation_type").HasMaxLength(64).IsRequired();
        b.Property(o => o.Category).HasColumnName("category").HasMaxLength(16).IsRequired().HasDefaultValue("review");
        b.Property(o => o.StartSeconds).HasColumnName("start_seconds");
        b.Property(o => o.EndSeconds).HasColumnName("end_seconds");
        b.Property(o => o.OccurredAt).HasColumnName("occurred_at");
        b.Property(o => o.Confidence).HasColumnName("confidence");
        b.Property(o => o.ConfidenceKind).HasColumnName("confidence_kind").HasMaxLength(32).IsRequired();
        b.Property(o => o.SupportingEvidenceJson).HasColumnName("supporting_evidence_json").IsRequired();
        b.Property(o => o.ModelName).HasColumnName("model_name").HasMaxLength(128).IsRequired();
        b.Property(o => o.ModelVersion).HasColumnName("model_version").HasMaxLength(128).IsRequired();
        b.Property(o => o.CreatedAt).HasColumnName("created_at");
        b.Property(o => o.StoredAt).HasColumnName("stored_at");
        b.HasOne<AudioEvent>().WithMany().HasForeignKey(o => new { o.SessionId, o.EventId }).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(o => new { o.SessionId, o.OccurredAt }).HasDatabaseName("ix_observations_session_time");
        b.HasIndex(o => new { o.SessionId, o.ObservationType }).HasDatabaseName("ix_observations_session_type");
    }
}

internal sealed class SessionVoiceprintConfiguration : IEntityTypeConfiguration<SessionVoiceprint>
{
    public void Configure(EntityTypeBuilder<SessionVoiceprint> b)
    {
        b.ToTable("session_voiceprints");
        b.HasKey(v => v.SessionId);
        b.Property(v => v.SessionId).HasColumnName("session_id").HasMaxLength(40);
        b.Property(v => v.EnrolmentEventId).HasColumnName("enrolment_event_id");
        b.Property(v => v.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        b.Property(v => v.Reason).HasColumnName("reason").HasMaxLength(500);
        b.Property(v => v.SpeechSeconds).HasColumnName("speech_seconds");
        b.Property(v => v.EmbeddingJson).HasColumnName("embedding_json");
        b.Property(v => v.Model).HasColumnName("model").HasMaxLength(128).IsRequired();
        b.Property(v => v.CreatedAt).HasColumnName("created_at");
        b.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        b.Ignore(v => v.IsUsable);
        b.HasOne<Session>().WithOne().HasForeignKey<SessionVoiceprint>(v => v.SessionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(v => v.UpdatedAt).HasDatabaseName("ix_session_voiceprints_updated");
    }
}

public sealed class EfObservationRepository(ProctoringDbContext db) : IObservationRepository
{
    public Task<bool> ExistsAsync(Guid observationId, CancellationToken ct) =>
        db.Observations.AnyAsync(o => o.Id == observationId, ct);

    public void Add(AudioObservation observation)
    {
        // Two observations with the same id in one result (should not happen) must not break the save.
        if (db.Observations.Local.Any(o => o.Id == observation.Id)) return;
        db.Observations.Add(observation);
    }

    public async Task<IReadOnlyList<AudioObservation>> ListAsync(string sessionId, int max, CancellationToken ct) =>
        await db.Observations.AsNoTracking()
            .Where(o => o.SessionId == sessionId)
            .OrderBy(o => o.OccurredAt).ThenBy(o => o.StartSeconds)
            .Take(max)
            .ToListAsync(ct);
}

public sealed class EfVoiceprintRepository(ProctoringDbContext db) : IVoiceprintRepository
{
    public Task<SessionVoiceprint?> FindAsync(string sessionId, CancellationToken ct) =>
        db.Voiceprints.FirstOrDefaultAsync(v => v.SessionId == sessionId, ct);

    public void Add(SessionVoiceprint voiceprint) => db.Voiceprints.Add(voiceprint);

    public Task<int> DeleteUpdatedBeforeAsync(DateTime cutoffUtc, CancellationToken ct) =>
        db.Voiceprints.Where(v => v.UpdatedAt < cutoffUtc).ExecuteDeleteAsync(ct);
}
