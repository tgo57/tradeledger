using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Executions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    Account = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "REAL", nullable: true),
                    Price = table.Column<decimal>(type: "REAL", nullable: true),
                    Fees = table.Column<decimal>(type: "REAL", nullable: false),
                    NetAmount = table.Column<decimal>(type: "REAL", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    SourceFile = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRowNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RawRowJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradeGroups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    Account = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyType = table.Column<string>(type: "TEXT", nullable: false),
                    Setup = table.Column<string>(type: "TEXT", nullable: false),
                    Underlying = table.Column<string>(type: "TEXT", nullable: false),
                    Expiration = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Right = table.Column<string>(type: "TEXT", nullable: false),
                    ShortStrike = table.Column<decimal>(type: "TEXT", nullable: false),
                    LongStrike = table.Column<decimal>(type: "TEXT", nullable: false),
                    OpenDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CloseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NetPL = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradeGroupExecutions",
                columns: table => new
                {
                    TradeGroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExecutionId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeGroupExecutions", x => new { x.TradeGroupId, x.ExecutionId });
                    table.ForeignKey(
                        name: "FK_TradeGroupExecutions_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TradeGroupExecutions_TradeGroups_TradeGroupId",
                        column: x => x.TradeGroupId,
                        principalTable: "TradeGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TradeGroupLegs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradeGroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    Underlying = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Expiration = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Right = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Strike = table.Column<decimal>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeGroupLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradeGroupLegs_TradeGroups_TradeGroupId",
                        column: x => x.TradeGroupId,
                        principalTable: "TradeGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_Broker_Account_ExecutedAt_Symbol",
                table: "Executions",
                columns: new[] { "Broker", "Account", "ExecutedAt", "Symbol" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_Fingerprint",
                table: "Executions",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradeGroupExecutions_ExecutionId",
                table: "TradeGroupExecutions",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeGroupLegs_TradeGroupId",
                table: "TradeGroupLegs",
                column: "TradeGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeGroups_Broker_Account_Underlying_Expiration_Right_ShortStrike_LongStrike_OpenDate",
                table: "TradeGroups",
                columns: new[] { "Broker", "Account", "Underlying", "Expiration", "Right", "ShortStrike", "LongStrike", "OpenDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradeGroupExecutions");

            migrationBuilder.DropTable(
                name: "TradeGroupLegs");

            migrationBuilder.DropTable(
                name: "Executions");

            migrationBuilder.DropTable(
                name: "TradeGroups");
        }
    }
}
