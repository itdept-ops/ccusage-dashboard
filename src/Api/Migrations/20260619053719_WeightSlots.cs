using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class WeightSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WeightEntries_UserEmail_LocalDate",
                table: "WeightEntries");

            migrationBuilder.AddColumn<int>(
                name: "Slot",
                table: "WeightEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_WeightEntries_UserEmail_LocalDate_Slot",
                table: "WeightEntries",
                columns: new[] { "UserEmail", "LocalDate", "Slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WeightEntries_UserEmail_LocalDate_Slot",
                table: "WeightEntries");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "WeightEntries");

            migrationBuilder.CreateIndex(
                name: "IX_WeightEntries_UserEmail_LocalDate",
                table: "WeightEntries",
                columns: new[] { "UserEmail", "LocalDate" },
                unique: true);
        }
    }
}
