using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class RenameBetaAccessToPlatformMobile : Migration
    {
        // Data-only migration: the mobile-platform gate was renamed `beta.access` -> `platform.mobile`, and the
        // legacy `tracker.beta` perm was retired (folded into the same gate). Rename existing grants so nobody
        // who has mobile access today (e.g. the owner + spouse) loses it. There is a UNIQUE (UserId, Permission)
        // index, so a user holding BOTH legacy keys must be de-duplicated before the rename, else the second
        // UPDATE collides.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) A user with both legacy keys → drop the tracker.beta row first (beta.access becomes platform.mobile).
            migrationBuilder.Sql(@"
                DELETE FROM ""UserPermissions"" up
                 WHERE up.""Permission"" = 'tracker.beta'
                   AND EXISTS (SELECT 1 FROM ""UserPermissions"" b
                                WHERE b.""UserId"" = up.""UserId"" AND b.""Permission"" = 'beta.access');");

            // 2) Rename the umbrella gate.
            migrationBuilder.Sql(@"UPDATE ""UserPermissions"" SET ""Permission"" = 'platform.mobile' WHERE ""Permission"" = 'beta.access';");

            // 3) Rename the remaining legacy tracker.beta grants (users who had it but NOT beta.access).
            migrationBuilder.Sql(@"UPDATE ""UserPermissions"" SET ""Permission"" = 'platform.mobile' WHERE ""Permission"" = 'tracker.beta';");

            // 4) Defensive: scrub the retired keys from the access-policy default CSV (they were never defaultable,
            //    but make the rename total). Handles the key in any CSV position.
            migrationBuilder.Sql(@"
                UPDATE ""AppConfigs"" SET ""DefaultPermissionsCsv"" =
                    trim(both ',' from regexp_replace(
                        replace(replace(',' || ""DefaultPermissionsCsv"" || ',', ',beta.access,', ','), ',tracker.beta,', ','),
                        ',+', ',', 'g'))
                 WHERE ""DefaultPermissionsCsv"" LIKE '%beta.access%' OR ""DefaultPermissionsCsv"" LIKE '%tracker.beta%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort reverse: restore the umbrella key name. The tracker.beta split cannot be reconstructed
            // (it was merged), so down-migrating leaves those users with beta.access only — acceptable.
            migrationBuilder.Sql(@"UPDATE ""UserPermissions"" SET ""Permission"" = 'beta.access' WHERE ""Permission"" = 'platform.mobile';");
        }
    }
}
