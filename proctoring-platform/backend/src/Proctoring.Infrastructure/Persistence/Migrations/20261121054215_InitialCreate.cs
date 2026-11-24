using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Proctoring.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    candidate_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    exam_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ended_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    end_reason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    token_hash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    client_user_agent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    client_app_version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    request_fingerprint = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_candidate_exam",
                table: "sessions",
                columns: new[] { "candidate_id", "exam_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_status_expires",
                table: "sessions",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_sessions_idempotency_key",
                table: "sessions",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
