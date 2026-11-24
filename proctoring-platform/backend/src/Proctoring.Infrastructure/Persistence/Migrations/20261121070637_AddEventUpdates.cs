using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Proctoring.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "audio_events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "event_updates",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_updates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_updates_created",
                table: "event_updates",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_event_updates_session_id",
                table: "event_updates",
                columns: new[] { "session_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_updates");

            migrationBuilder.DropColumn(
                name: "version",
                table: "audio_events");
        }
    }
}
