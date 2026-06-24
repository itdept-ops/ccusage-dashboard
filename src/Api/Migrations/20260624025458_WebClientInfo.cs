using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class WebClientInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ColorDepth",
                table: "LoginEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeviceMemory",
                table: "LoginEvents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DevicePixelRatio",
                table: "LoginEvents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HardwareConcurrency",
                table: "LoginEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Languages",
                table: "LoginEvents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "LoginEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenHeight",
                table: "LoginEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenWidth",
                table: "LoginEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "LoginEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TouchPoints",
                table: "LoginEvents",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ColorDepth",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "DeviceMemory",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "DevicePixelRatio",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "HardwareConcurrency",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "Languages",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "ScreenHeight",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "ScreenWidth",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "LoginEvents");

            migrationBuilder.DropColumn(
                name: "TouchPoints",
                table: "LoginEvents");
        }
    }
}
