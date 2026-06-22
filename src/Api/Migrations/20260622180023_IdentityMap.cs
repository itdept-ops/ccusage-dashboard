using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class IdentityMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityTimeEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Minutes = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceEventId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Note = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityTimeEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRoles_UserEmail_Name",
                table: "IdentityRoles",
                columns: new[] { "UserEmail", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRules_RoleId",
                table: "IdentityRules",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRules_UserEmail_Keyword",
                table: "IdentityRules",
                columns: new[] { "UserEmail", "Keyword" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityTimeEntries_RoleId",
                table: "IdentityTimeEntries",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityTimeEntries_UserEmail_Date",
                table: "IdentityTimeEntries",
                columns: new[] { "UserEmail", "Date" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityTimeEntries_UserEmail_SourceEventId",
                table: "IdentityTimeEntries",
                columns: new[] { "UserEmail", "SourceEventId" },
                unique: true,
                filter: "\"SourceEventId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityRoles");

            migrationBuilder.DropTable(
                name: "IdentityRules");

            migrationBuilder.DropTable(
                name: "IdentityTimeEntries");
        }
    }
}
