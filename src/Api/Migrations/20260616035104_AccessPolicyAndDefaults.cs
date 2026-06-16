using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class AccessPolicyAndDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultPermissionsCsv",
                table: "AppConfigs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "dashboard.view");

            migrationBuilder.AddColumn<bool>(
                name: "OpenSignupEnabled",
                table: "AppConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // One-time backfill. Under the old model a single "dashboard.view" unlocked the Dashboard,
            // Calendar, Pricing and Settings (read) pages plus CSV export. The granular split would
            // otherwise silently strip those pages from existing non-admin users, so grant the
            // equivalent view keys to anyone who currently holds dashboard.view. Admins already hold
            // every key, so the NOT EXISTS guard makes this a no-op for them; it is also idempotent.
            migrationBuilder.Sql(@"
                INSERT INTO ""UserPermissions"" (""UserId"", ""Permission"")
                SELECT up.""UserId"", v.perm
                FROM ""UserPermissions"" up
                CROSS JOIN (VALUES ('calendar.view'), ('pricing.view'), ('settings.view'), ('dashboard.export')) AS v(perm)
                WHERE up.""Permission"" = 'dashboard.view'
                  AND NOT EXISTS (
                    SELECT 1 FROM ""UserPermissions"" ux
                    WHERE ux.""UserId"" = up.""UserId"" AND ux.""Permission"" = v.perm
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultPermissionsCsv",
                table: "AppConfigs");

            migrationBuilder.DropColumn(
                name: "OpenSignupEnabled",
                table: "AppConfigs");
        }
    }
}
