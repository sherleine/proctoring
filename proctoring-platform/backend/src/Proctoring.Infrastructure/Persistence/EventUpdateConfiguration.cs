using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Proctoring.Domain.Events;

namespace Proctoring.Infrastructure.Persistence;

internal sealed class EventUpdateConfiguration : IEntityTypeConfiguration<EventUpdate>
{
    public void Configure(EntityTypeBuilder<EventUpdate> b)
    {
        b.ToTable("event_updates");
        b.HasKey(u => u.Id);
        // AUTOINCREMENT: ids are never reused, even after pruning, so Last-Event-ID stays meaningful.
        b.Property(u => u.Id).HasColumnName("id").ValueGeneratedOnAdd().HasAnnotation("Sqlite:Autoincrement", true);
        b.Property(u => u.SessionId).HasColumnName("session_id").HasMaxLength(40).IsRequired();
        b.Property(u => u.EventId).HasColumnName("event_id");
        b.Property(u => u.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(u => u.CreatedAt).HasColumnName("created_at");
        b.HasIndex(u => new { u.SessionId, u.Id }).HasDatabaseName("ix_event_updates_session_id");
        b.HasIndex(u => u.CreatedAt).HasDatabaseName("ix_event_updates_created");
    }
}
