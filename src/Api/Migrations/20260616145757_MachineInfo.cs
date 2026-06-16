using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class MachineInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    LocalIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublicIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Os = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Arch = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Hostname = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OsUser = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Agent = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReporterVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CpuCount = table.Column<int>(type: "integer", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineInfos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineInfos_Name",
                table: "MachineInfos",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineInfos");
        }
    }
}
