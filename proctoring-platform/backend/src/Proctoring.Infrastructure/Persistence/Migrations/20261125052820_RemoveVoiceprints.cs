using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Proctoring.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveVoiceprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_voiceprints");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_voiceprints",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    embedding_json = table.Column<string>(type: "TEXT", nullable: true),
                    enrolment_event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    speech_seconds = table.Column<double>(type: "REAL", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
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
                name: "ix_session_voiceprints_updated",
                table: "session_voiceprints",
                column: "updated_at");
        }
    }
}
