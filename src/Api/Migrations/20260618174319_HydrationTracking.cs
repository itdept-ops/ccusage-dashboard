using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class HydrationTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HydrationGoalMl",
                table: "TrackerProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HydrationEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMl = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HydrationEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HydrationEntries_UserEmail_LocalDate",
                table: "HydrationEntries",
                columns: new[] { "UserEmail", "LocalDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HydrationEntries");

            migrationBuilder.DropColumn(
                name: "HydrationGoalMl",
                table: "TrackerProfiles");
        }
    }
}
