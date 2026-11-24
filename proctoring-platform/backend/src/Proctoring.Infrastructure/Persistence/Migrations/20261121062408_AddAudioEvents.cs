using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Proctoring.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audio_events",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    candidate_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    exam_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    sequence_number = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ended_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    speech_started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: false),
                    audio_format = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    audio_codec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    sample_rate = table.Column<int>(type: "INTEGER", nullable: false),
                    channels = table.Column<int>(type: "INTEGER", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    audio_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    metadata_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    metadata_json = table.Column<string>(type: "TEXT", nullable: false),
                    storage_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    audio_deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    received_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    queued_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    result_json = table.Column<string>(type: "TEXT", nullable: true),
                    publish_lease_until = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_events", x => new { x.session_id, x.event_id });
                    table.ForeignKey(
                        name: "FK_audio_events_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audio_events_received",
                table: "audio_events",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_audio_events_session_sequence",
                table: "audio_events",
                columns: new[] { "session_id", "sequence_number" });

            migrationBuilder.CreateIndex(
                name: "ix_audio_events_status_received",
                table: "audio_events",
                columns: new[] { "status", "received_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audio_events");
        }
    }
}
