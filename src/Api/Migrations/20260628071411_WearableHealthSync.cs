using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class WearableHealthSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "SleepEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "ExerciseEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RestingHeartRate",
                table: "DailyActivities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "DailyActivities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "HealthConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    EncryptedRefreshToken = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Scope = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProviderUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AutoSyncEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SyncSteps = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SyncSleep = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SyncHeartRate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SyncWorkouts = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LastSyncCursorDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastSyncUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<int>(type: "integer", nullable: false),
                    ConnectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthImportLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SignalKind = table.Column<int>(type: "integer", nullable: false),
                    SourceRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TrackerEntityId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthImportLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthConnections_AutoSyncEnabled_LastSyncUtc",
                table: "HealthConnections",
                columns: new[] { "AutoSyncEnabled", "LastSyncUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthConnections_UserId_Provider",
                table: "HealthConnections",
                columns: new[] { "UserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthImportLogs_UserEmail_Provider_LocalDate",
                table: "HealthImportLogs",
                columns: new[] { "UserEmail", "Provider", "LocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthImportLogs_UserEmail_Provider_SignalKind_SourceRef",
                table: "HealthImportLogs",
                columns: new[] { "UserEmail", "Provider", "SignalKind", "SourceRef" },
                unique: true,
                filter: "\"SourceRef\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthConnections");

            migrationBuilder.DropTable(
                name: "HealthImportLogs");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "SleepEntries");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ExerciseEntries");

            migrationBuilder.DropColumn(
                name: "RestingHeartRate",
                table: "DailyActivities");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "DailyActivities");
        }
    }
}
