using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class TrxList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    XsollaTxId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    TransactionType = table.Column<string>(type: "text", maxLength: 32, nullable: false),
                    PlanExternalId = table.Column<string>(type: "text", maxLength: 32, nullable: true),
                    BoostPackType = table.Column<string>(type: "text", maxLength: 32, nullable: true),
                    BoostCount = table.Column<int>(type: "integer", nullable: true),
                    Amount = table.Column<string>(type: "text", maxLength: 32, nullable: true),
                    Currency = table.Column<string>(type: "text", maxLength: 8, nullable: true),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_UserId",
                table: "PaymentTransactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_XsollaTxId",
                table: "PaymentTransactions",
                column: "XsollaTxId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentTransactions");
        }
    }
}
