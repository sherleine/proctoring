using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Proctoring.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HashedIdempotencyAndReplayableTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_sessions_idempotency_key",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "sessions");

            migrationBuilder.AddColumn<DateTime>(
                name: "idempotency_expires_at",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "idempotency_key_hash",
                table: "sessions",
                type: "BLOB",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "token_derivation_salt",
                table: "sessions",
                type: "BLOB",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_sessions_idempotency_key_hash",
                table: "sessions",
                column: "idempotency_key_hash",
                unique: true,
                filter: "idempotency_key_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_sessions_idempotency_key_hash",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "idempotency_expires_at",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "idempotency_key_hash",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "token_derivation_salt",
                table: "sessions");

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "sessions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_sessions_idempotency_key",
                table: "sessions",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }
    }
}
