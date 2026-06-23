using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class WeeklyRecapOptIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastRecapSent",
                table: "NotificationPreferences",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyRecapEnabled",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRecapSent",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "WeeklyRecapEnabled",
                table: "NotificationPreferences");
        }
    }
}
