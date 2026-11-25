using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Proctoring.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddObservationsAndVoiceprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    observation_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    start_seconds = table.Column<double>(type: "REAL", nullable: false),
                    end_seconds = table.Column<double>(type: "REAL", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    confidence = table.Column<double>(type: "REAL", nullable: false),
                    confidence_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    supporting_evidence_json = table.Column<string>(type: "TEXT", nullable: false),
                    model_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    model_version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    stored_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observations", x => x.id);
                    table.ForeignKey(
                        name: "FK_observations_audio_events_session_id_event_id",
                        columns: x => new { x.session_id, x.event_id },
                        principalTable: "audio_events",
                        principalColumns: new[] { "session_id", "event_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_voiceprints",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    enrolment_event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    speech_seconds = table.Column<double>(type: "REAL", nullable: false),
                    embedding_json = table.Column<string>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_voiceprints", x => x.session_id);
                    table.ForeignKey(
                        name: "FK_session_voiceprints_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_observations_session_id_event_id",
                table: "observations",
                columns: new[] { "session_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_observations_session_time",
                table: "observations",
                columns: new[] { "session_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_observations_session_type",
                table: "observations",
                columns: new[] { "session_id", "observation_type" });

            migrationBuilder.CreateIndex(
                name: "ix_session_voiceprints_updated",
                table: "session_voiceprints",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "observations");

            migrationBuilder.DropTable(
                name: "session_voiceprints");
        }
    }
}
