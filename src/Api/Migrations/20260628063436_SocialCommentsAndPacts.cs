using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class SocialCommentsAndPacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityComments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActivityEventId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityComments_ActivityEvents_ActivityEventId",
                        column: x => x.ActivityEventId,
                        principalTable: "ActivityEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HabitPacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetIntValue = table.Column<int>(type: "integer", nullable: false),
                    PeriodDays = table.Column<int>(type: "integer", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HabitPacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HabitPactMembers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HabitPactId = table.Column<long>(type: "bigint", nullable: false),
                    MemberEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    JoinedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HabitPactMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HabitPactMembers_HabitPacts_HabitPactId",
                        column: x => x.HabitPactId,
                        principalTable: "HabitPacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityComments_ActivityEventId_CreatedUtc",
                table: "ActivityComments",
                columns: new[] { "ActivityEventId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HabitPactMembers_HabitPactId_MemberEmail",
                table: "HabitPactMembers",
                columns: new[] { "HabitPactId", "MemberEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HabitPactMembers_MemberEmail",
                table: "HabitPactMembers",
                column: "MemberEmail");

            migrationBuilder.CreateIndex(
                name: "IX_HabitPacts_OwnerEmail",
                table: "HabitPacts",
                column: "OwnerEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityComments");

            migrationBuilder.DropTable(
                name: "HabitPactMembers");

            migrationBuilder.DropTable(
                name: "HabitPacts");
        }
    }
}
