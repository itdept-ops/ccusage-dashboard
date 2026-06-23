using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class GeminiPricingSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ModelPricings",
                columns: new[] { "Id", "CacheReadPerMTok", "CacheWrite1hPerMTok", "CacheWrite5mPerMTok", "DisplayName", "InputPerMTok", "IsPlaceholder", "ModelPattern", "OutputPerMTok" },
                values: new object[,]
                {
                    { 10, 0.075m, 0m, 0m, "Gemini 2.5 Flash", 0.30m, false, "gemini-2.5-flash", 2.50m },
                    { 11, 0.075m, 0m, 0m, "Other Gemini (estimated)", 0.30m, false, "gemini-", 2.50m }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "ModelPricings",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "ModelPricings",
                keyColumn: "Id",
                keyValue: 11);
        }
    }
}
