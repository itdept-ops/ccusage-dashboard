using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class FamilyRemindersTimers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FamilyReminders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    TargetUserId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Recurrence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    LastFiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyReminders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FamilyTimers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    StartedByUserId = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EndsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Done = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyTimers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FamilyReminders_Active_DueUtc",
                table: "FamilyReminders",
                columns: new[] { "Active", "DueUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FamilyReminders_HouseholdId",
                table: "FamilyReminders",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyTimers_Done_EndsUtc",
                table: "FamilyTimers",
                columns: new[] { "Done", "EndsUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FamilyTimers_HouseholdId",
                table: "FamilyTimers",
                column: "HouseholdId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FamilyReminders");

            migrationBuilder.DropTable(
                name: "FamilyTimers");
        }
    }
}
