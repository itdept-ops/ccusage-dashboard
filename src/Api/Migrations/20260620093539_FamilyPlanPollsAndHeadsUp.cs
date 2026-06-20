using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class FamilyPlanPollsAndHeadsUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EventHeadsUpEnabled",
                table: "Households",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "EventHeadsUpLeadMinutes",
                table: "Households",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateTable(
                name: "FamilyEventAnnouncements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    GoogleEventId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    EventStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnnouncedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyEventAnnouncements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FamilyPlanPolls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "time"),
                    Closed = table.Column<bool>(type: "boolean", nullable: false),
                    WinningOptionId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyPlanPolls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FamilyPlanPollOptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PollId = table.Column<long>(type: "bigint", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyPlanPollOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamilyPlanPollOptions_FamilyPlanPolls_PollId",
                        column: x => x.PollId,
                        principalTable: "FamilyPlanPolls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FamilyPlanPollVotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OptionId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyPlanPollVotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamilyPlanPollVotes_FamilyPlanPollOptions_OptionId",
                        column: x => x.OptionId,
                        principalTable: "FamilyPlanPollOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FamilyEventAnnouncements_HouseholdId_GoogleEventId",
                table: "FamilyEventAnnouncements",
                columns: new[] { "HouseholdId", "GoogleEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FamilyPlanPollOptions_PollId",
                table: "FamilyPlanPollOptions",
                column: "PollId");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyPlanPolls_HouseholdId",
                table: "FamilyPlanPolls",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyPlanPollVotes_OptionId_UserId",
                table: "FamilyPlanPollVotes",
                columns: new[] { "OptionId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FamilyEventAnnouncements");

            migrationBuilder.DropTable(
                name: "FamilyPlanPollVotes");

            migrationBuilder.DropTable(
                name: "FamilyPlanPollOptions");

            migrationBuilder.DropTable(
                name: "FamilyPlanPolls");

            migrationBuilder.DropColumn(
                name: "EventHeadsUpEnabled",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "EventHeadsUpLeadMinutes",
                table: "Households");
        }
    }
}
