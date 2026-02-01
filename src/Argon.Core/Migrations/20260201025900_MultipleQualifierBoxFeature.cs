using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class MultipleQualifierBoxFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MultipleQualifierBoxKey",
                table: "Items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid[]>(
                name: "ReferenceItemIds",
                table: "ItemUseScenario",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_MultipleQualifierBoxKey",
                table: "Items",
                column: "MultipleQualifierBoxKey");

            migrationBuilder.AddForeignKey(
                name: "FK_Items_ItemUseScenario_MultipleQualifierBoxKey",
                table: "Items",
                column: "MultipleQualifierBoxKey",
                principalTable: "ItemUseScenario",
                principalColumn: "Key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Items_ItemUseScenario_MultipleQualifierBoxKey",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_MultipleQualifierBoxKey",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "MultipleQualifierBoxKey",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ReferenceItemIds",
                table: "ItemUseScenario");
        }
    }
}
