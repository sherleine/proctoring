using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Proctoring.Domain.Events;
using Proctoring.Domain.Sessions;

namespace Proctoring.Infrastructure.Persistence;

/// <summary>EF Core database context. Each DbSet is a table; the schema is created by migrations.</summary>
public sealed class ProctoringDbContext(DbContextOptions<ProctoringDbContext> options) : DbContext(options)
{
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AudioEvent> AudioEvents => Set<AudioEvent>();
    public DbSet<EventUpdate> EventUpdates => Set<EventUpdate>();
    public DbSet<AudioObservation> Observations => Set<AudioObservation>();

    /// <summary>Shadow concurrency column of audio_events.</summary>
    public const string VersionProperty = "Version";

    /// <summary>Sessions whose events changed in the last successful save (used to wake live-update streams).</summary>
    public IReadOnlyCollection<string> LastChangedSessions { get; private set; } = [];

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new AudioEventConfiguration());
        modelBuilder.ApplyConfiguration(new EventUpdateConfiguration());
        modelBuilder.ApplyConfiguration(new AudioObservationConfiguration());
    }

    // Every save that adds an event or changes its status/result/error/audio also appends a change-log entry.
    // Both are written by the same SaveChanges call, i.e. in one database transaction (transactional outbox):
    // a state change is never committed without its update entry, and vice versa.

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var sessions = AppendEventUpdates();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        LastChangedSessions = sessions;
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var sessions = AppendEventUpdates();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        LastChangedSessions = sessions;
        return result;
    }

    private static readonly string[] TrackedProperties =
        [nameof(AudioEvent.Status), nameof(AudioEvent.ResultJson), nameof(AudioEvent.ErrorCode), nameof(AudioEvent.StorageKey)];

    private HashSet<string> AppendEventUpdates()
    {
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ChangeTracker.Entries<AudioEvent>().ToList())
        {
            if (entry.State == EntityState.Modified)
            {
                var version = entry.Property<long>(VersionProperty);
                version.CurrentValue = version.OriginalValue + 1;
            }
            var relevant = entry.State == EntityState.Added
                           || (entry.State == EntityState.Modified && TrackedProperties.Any(p => entry.Property(p).IsModified));
            if (!relevant) continue;
            EventUpdates.Add(EventUpdate.For(entry.Entity, entry.Entity.UpdatedAt));
            sessions.Add(entry.Entity.SessionId);
        }
        return sessions;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no time-zone-aware type: store UTC and mark values as UTC when reading back.
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("sessions");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).HasColumnName("id").HasMaxLength(40);
        b.Property(s => s.CandidateId).HasColumnName("candidate_id").HasMaxLength(128).IsRequired();
        b.Property(s => s.ExamId).HasColumnName("exam_id").HasMaxLength(128).IsRequired();
        b.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(s => s.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.Property(s => s.EndedAt).HasColumnName("ended_at");
        b.Property(s => s.EndReason).HasColumnName("end_reason").HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.TokenHash).HasColumnName("token_hash").HasMaxLength(Session.TokenHashLength).IsRequired();
        b.Property(s => s.ClientUserAgent).HasColumnName("client_user_agent").HasMaxLength(512);
        b.Property(s => s.ClientAppVersion).HasColumnName("client_app_version").HasMaxLength(32);
        // Idempotency: only hashes of the key; the salt exists only while a replay is allowed.
        b.Property(s => s.IdempotencyKeyHash).HasColumnName("idempotency_key_hash").HasMaxLength(Session.HashLength);
        b.Property(s => s.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(Session.HashLength);
        b.Property(s => s.TokenDerivationSalt).HasColumnName("token_derivation_salt").HasMaxLength(Session.DerivationSaltLength);
        b.Property(s => s.IdempotencyExpiresAt).HasColumnName("idempotency_expires_at");

        b.HasIndex(s => s.IdempotencyKeyHash).IsUnique().HasFilter("idempotency_key_hash IS NOT NULL")
            .HasDatabaseName("ux_sessions_idempotency_key_hash");
        b.HasIndex(s => new { s.CandidateId, s.ExamId }).HasDatabaseName("ix_sessions_candidate_exam");
        b.HasIndex(s => new { s.Status, s.ExpiresAt }).HasDatabaseName("ix_sessions_status_expires");
    }
}
