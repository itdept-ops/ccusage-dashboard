using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class TrackerFullFitness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActivityLevel",
                table: "TrackerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                table: "TrackerProfiles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GoalWeightKg",
                table: "TrackerProfiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HeightCm",
                table: "TrackerProfiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Sex",
                table: "TrackerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UnitSystem",
                table: "TrackerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "WeightEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WeightKg = table.Column<double>(type: "double precision", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeightEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeightEntries_UserEmail_LocalDate",
                table: "WeightEntries",
                columns: new[] { "UserEmail", "LocalDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeightEntries");

            migrationBuilder.DropColumn(
                name: "ActivityLevel",
                table: "TrackerProfiles");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                table: "TrackerProfiles");

            migrationBuilder.DropColumn(
                name: "GoalWeightKg",
                table: "TrackerProfiles");

            migrationBuilder.DropColumn(
                name: "HeightCm",
                table: "TrackerProfiles");

            migrationBuilder.DropColumn(
                name: "Sex",
                table: "TrackerProfiles");

            migrationBuilder.DropColumn(
                name: "UnitSystem",
                table: "TrackerProfiles");
        }
    }
}
