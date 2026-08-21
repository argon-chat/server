using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class TrxXsollaDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CardBrand",
                table: "PaymentTransactions",
                type: "text",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardSuffix",
                table: "PaymentTransactions",
                type: "text",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PaymentAccountId",
                table: "PaymentTransactions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "PaymentTransactions",
                type: "text",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CardBrand",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "CardSuffix",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "PaymentAccountId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "PaymentTransactions");
        }
    }
}
