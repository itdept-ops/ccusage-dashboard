using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class MachineTelemetryAndGps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AccuracyM",
                table: "MachineInfos",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CpuModel",
                table: "MachineInfos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Culture",
                table: "MachineInfos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "MachineInfos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FrameworkVersion",
                table: "MachineInfos",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeoSource",
                table: "MachineInfos",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GpuModel",
                table: "MachineInfos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LanIps",
                table: "MachineInfos",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LogicalCores",
                table: "MachineInfos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineGuid",
                table: "MachineInfos",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Manufacturer",
                table: "MachineInfos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "MachineInfos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhysicalCores",
                table: "MachineInfos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RamTotalMB",
                table: "MachineInfos",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "MachineInfos",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UptimeSec",
                table: "MachineInfos",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccuracyM",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "CpuModel",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "Culture",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "FrameworkVersion",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "GeoSource",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "GpuModel",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "LanIps",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "LogicalCores",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "MachineGuid",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "Manufacturer",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "PhysicalCores",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "RamTotalMB",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "MachineInfos");

            migrationBuilder.DropColumn(
                name: "UptimeSec",
                table: "MachineInfos");
        }
    }
}
