using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSourcesAndCodex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "UsageRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "claude-code");

            migrationBuilder.CreateTable(
                name: "IngestionSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RootPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionSources", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ModelPricings",
                columns: new[] { "Id", "CacheReadPerMTok", "CacheWrite1hPerMTok", "CacheWrite5mPerMTok", "DisplayName", "InputPerMTok", "IsPlaceholder", "ModelPattern", "OutputPerMTok" },
                values: new object[,]
                {
                    { 6, 0.125m, 0m, 0m, "GPT-5.5 (placeholder)", 1.25m, true, "gpt-5.5", 10.00m },
                    { 7, 0.125m, 0m, 0m, "GPT-5.4 (placeholder)", 1.25m, true, "gpt-5.4", 10.00m },
                    { 8, 0.05m, 0m, 0m, "GPT-5.3 Codex (placeholder)", 0.50m, true, "gpt-5.3-codex", 4.00m },
                    { 9, 0.125m, 0m, 0m, "Other GPT (placeholder)", 1.25m, true, "gpt-", 10.00m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_Source",
                table: "UsageRecords",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionSources_Name",
                table: "IngestionSources",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestionSources");

            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_Source",
                table: "UsageRecords");

            migrationBuilder.DeleteData(
                table: "ModelPricings",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "ModelPricings",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "ModelPricings",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "ModelPricings",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DropColumn(
                name: "Source",
                table: "UsageRecords");
        }
    }
}
