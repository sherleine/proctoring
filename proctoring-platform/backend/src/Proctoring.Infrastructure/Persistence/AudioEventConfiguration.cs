using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;

namespace Proctoring.Infrastructure.Persistence;

internal sealed class AudioEventConfiguration : IEntityTypeConfiguration<AudioEvent>
{
    public void Configure(EntityTypeBuilder<AudioEvent> b)
    {
        b.ToTable("audio_events");
        // Event IDs are unique per session only: the key is (session_id, event_id).
        b.HasKey(e => new { e.SessionId, e.EventId });
        b.HasOne<Session>().WithMany().HasForeignKey(e => e.SessionId).OnDelete(DeleteBehavior.Restrict);

        b.Property(e => e.SessionId).HasColumnName("session_id").HasMaxLength(40);
        b.Property(e => e.EventId).HasColumnName("event_id");
        b.Property(e => e.CandidateId).HasColumnName("candidate_id").HasMaxLength(128).IsRequired();
        b.Property(e => e.ExamId).HasColumnName("exam_id").HasMaxLength(128).IsRequired();
        b.Property(e => e.SequenceNumber).HasColumnName("sequence_number");
        b.Property(e => e.StartedAt).HasColumnName("started_at");
        b.Property(e => e.EndedAt).HasColumnName("ended_at");
        b.Property(e => e.SpeechStartedAt).HasColumnName("speech_started_at");
        b.Property(e => e.DurationSeconds).HasColumnName("duration_seconds");
        b.Property(e => e.AudioFormat).HasColumnName("audio_format").HasMaxLength(16).IsRequired();
        b.Property(e => e.AudioCodec).HasColumnName("audio_codec").HasMaxLength(32).IsRequired();
        b.Property(e => e.SampleRate).HasColumnName("sample_rate");
        b.Property(e => e.Channels).HasColumnName("channels");
        b.Property(e => e.SizeBytes).HasColumnName("size_bytes");
        b.Property(e => e.AudioSha256).HasColumnName("audio_sha256").HasMaxLength(64).IsRequired();
        b.Property(e => e.MetadataSha256).HasColumnName("metadata_sha256").HasMaxLength(64).IsRequired();
        b.Property(e => e.MetadataJson).HasColumnName("metadata_json").IsRequired();
        b.Property(e => e.StorageKey).HasColumnName("storage_key").HasMaxLength(200);
        b.Property(e => e.AudioDeletedAt).HasColumnName("audio_deleted_at");
        b.Property(e => e.ReceivedAt).HasColumnName("received_at");
        b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        // Optimistic concurrency: "version" is incremented by every UPDATE (ProctoringDbContext / ExecuteUpdate),
        // so a save based on a stale read (e.g. two backend instances applying results for the same event)
        // fails instead of overwriting the newer state. Infrastructure-only (shadow property).
        b.Property<long>(ProctoringDbContext.VersionProperty).HasColumnName("version").HasDefaultValue(0L).IsConcurrencyToken();
        b.Property(e => e.QueuedAt).HasColumnName("queued_at");
        b.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(e => e.Attempts).HasColumnName("attempts");
        b.Property(e => e.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        b.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(1000);
        b.Property(e => e.ResultJson).HasColumnName("result_json");
        b.Property(e => e.PublishLeaseUntil).HasColumnName("publish_lease_until");

        b.HasIndex(e => new { e.SessionId, e.SequenceNumber }).HasDatabaseName("ix_audio_events_session_sequence");
        b.HasIndex(e => new { e.Status, e.ReceivedAt }).HasDatabaseName("ix_audio_events_status_received");
        b.HasIndex(e => e.ReceivedAt).HasDatabaseName("ix_audio_events_received");
    }
}
