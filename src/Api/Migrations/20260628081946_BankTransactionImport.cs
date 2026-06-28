using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ccusage.Api.Migrations
{
    /// <inheritdoc />
    public partial class BankTransactionImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FinanceTransactions_HouseholdId_DedupHash",
                table: "FinanceTransactions");

            migrationBuilder.AddColumn<string>(
                name: "Fitid",
                table: "FinanceTransactions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommittedUtc",
                table: "FinanceImports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "FinanceImports",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "rocketmoney");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "FinanceImports",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "staged");

            migrationBuilder.CreateTable(
                name: "FinanceCategoryRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    MatchType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "equals"),
                    Pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceCategoryRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FinanceStagedTransactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    ImportId = table.Column<long>(type: "bigint", nullable: false),
                    RowIndex = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Merchant = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RawAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Magnitude = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "expense"),
                    AccountKey = table.Column<string>(type: "character varying(420)", maxLength: 420, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Institution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccountTypeRaw = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SuggestedCategory = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CategorySource = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "none"),
                    Fitid = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DedupHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false),
                    Excluded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceStagedTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinanceStagedTransactions_FinanceImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "FinanceImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceTransactions_HouseholdId_DedupHash",
                table: "FinanceTransactions",
                columns: new[] { "HouseholdId", "DedupHash" },
                unique: true,
                filter: "\"Fitid\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FinanceTransactions_HouseholdId_Fitid",
                table: "FinanceTransactions",
                columns: new[] { "HouseholdId", "Fitid" },
                unique: true,
                filter: "\"Fitid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FinanceCategoryRules_HouseholdId",
                table: "FinanceCategoryRules",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_FinanceStagedTransactions_HouseholdId_ImportId",
                table: "FinanceStagedTransactions",
                columns: new[] { "HouseholdId", "ImportId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceStagedTransactions_ImportId",
                table: "FinanceStagedTransactions",
                column: "ImportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinanceCategoryRules");

            migrationBuilder.DropTable(
                name: "FinanceStagedTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinanceTransactions_HouseholdId_DedupHash",
                table: "FinanceTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinanceTransactions_HouseholdId_Fitid",
                table: "FinanceTransactions");

            migrationBuilder.DropColumn(
                name: "Fitid",
                table: "FinanceTransactions");

            migrationBuilder.DropColumn(
                name: "CommittedUtc",
                table: "FinanceImports");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "FinanceImports");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "FinanceImports");

            migrationBuilder.CreateIndex(
                name: "IX_FinanceTransactions_HouseholdId_DedupHash",
                table: "FinanceTransactions",
                columns: new[] { "HouseholdId", "DedupHash" },
                unique: true);
        }
    }
}
