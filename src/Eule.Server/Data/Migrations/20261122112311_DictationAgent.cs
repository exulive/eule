using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Eule.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DictationAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DictationLimit",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Body",
                table: "notes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "DictationId",
                table: "notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "notes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "todo");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "dictations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "dictations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "dictations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_notes_DictationId",
                table: "notes",
                column: "DictationId");

            migrationBuilder.CreateIndex(
                name: "IX_dictations_UserId_ReceivedAt",
                table: "dictations",
                columns: new[] { "UserId", "ReceivedAt" });

            // Existing notes: link each to the dictation it came from; the ones with a date belong to the agenda.
            migrationBuilder.Sql("""
                UPDATE notes n SET "DictationId" = d."Id" FROM dictations d WHERE d."NoteId" = n."Id";
                UPDATE notes SET "Kind" = 'event' WHERE "DueAt" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notes_DictationId",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "IX_dictations_UserId_ReceivedAt",
                table: "dictations");

            migrationBuilder.DropColumn(
                name: "DictationLimit",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Body",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "DictationId",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "dictations");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "dictations");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "dictations");
        }
    }
}
