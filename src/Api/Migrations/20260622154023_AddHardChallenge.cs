using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHardChallenge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HardChallenges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Ruleset = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CompletedDays = table.Column<int>(type: "integer", nullable: false),
                    CurrentStreak = table.Column<int>(type: "integer", nullable: false),
                    LongestStreak = table.Column<int>(type: "integer", nullable: false),
                    ConfessionsUsed = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HardChallengeDays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChallengeId = table.Column<int>(type: "integer", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DietOk = table.Column<bool>(type: "boolean", nullable: false),
                    DietOverride = table.Column<bool>(type: "boolean", nullable: true),
                    WaterGallonOk = table.Column<bool>(type: "boolean", nullable: false),
                    Workout1Ok = table.Column<bool>(type: "boolean", nullable: false),
                    Workout2Ok = table.Column<bool>(type: "boolean", nullable: false),
                    Workout2Outdoor = table.Column<bool>(type: "boolean", nullable: false),
                    ReadOk = table.Column<bool>(type: "boolean", nullable: false),
                    PhotoTaken = table.Column<bool>(type: "boolean", nullable: false),
                    NoAlcohol = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Confession = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    IsCheatDay = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardChallengeDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HardChallengeDays_HardChallenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "HardChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HardChallengeDays_ChallengeId",
                table: "HardChallengeDays",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_HardChallengeDays_UserEmail_LocalDate",
                table: "HardChallengeDays",
                columns: new[] { "UserEmail", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HardChallenges_UserEmail",
                table: "HardChallenges",
                column: "UserEmail",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HardChallengeDays");

            migrationBuilder.DropTable(
                name: "HardChallenges");
        }
    }
}
