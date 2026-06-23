using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class UserDisplayNamePresencePrefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AppearOffline",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DisplayNameMode",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "Nickname",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PresenceStatus",
                table: "Users",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShareAutoContext",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppearOffline",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DisplayNameMode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Nickname",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PresenceStatus",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ShareAutoContext",
                table: "Users");
        }
    }
}
