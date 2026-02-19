using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeGroupGrossReturn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "GrossReturn",
                table: "TradeGroups",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrossReturn",
                table: "TradeGroups");
        }
    }
}
