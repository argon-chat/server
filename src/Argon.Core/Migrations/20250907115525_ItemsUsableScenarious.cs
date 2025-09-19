using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class ItemsUsableScenarious : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Data",
                table: "Items");

            migrationBuilder.RenameColumn(
                name: "ItemUseVector",
                table: "Items",
                newName: "UseVector");

            migrationBuilder.AddColumn<Guid>(
                name: "ScenarioKey",
                table: "Items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemUseScenario",
                columns: table => new
                {
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioType = table.Column<string>(type: "character varying(21)", maxLength: 21, nullable: false),
                    Edition = table.Column<string>(type: "text", nullable: true),
                    PlanId = table.Column<string>(type: "text", nullable: true),
                    ReferenceItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    ServiceKey = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemUseScenario", x => x.Key);
                    table.ForeignKey(
                        name: "FK_ItemUseScenario_Items_ReferenceItemId",
                        column: x => x.ReferenceItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Items_Id_OwnerId",
                table: "Items",
                columns: new[] { "Id", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_ScenarioKey",
                table: "Items",
                column: "ScenarioKey");

            migrationBuilder.CreateIndex(
                name: "IX_ItemUseScenario_ReferenceItemId",
                table: "ItemUseScenario",
                column: "ReferenceItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_Items_ItemUseScenario_ScenarioKey",
                table: "Items",
                column: "ScenarioKey",
                principalTable: "ItemUseScenario",
                principalColumn: "Key",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Items_ItemUseScenario_ScenarioKey",
                table: "Items");

            migrationBuilder.DropTable(
                name: "ItemUseScenario");

            migrationBuilder.DropIndex(
                name: "IX_Items_Id_OwnerId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_ScenarioKey",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ScenarioKey",
                table: "Items");

            migrationBuilder.RenameColumn(
                name: "UseVector",
                table: "Items",
                newName: "ItemUseVector");

            migrationBuilder.AddColumn<string>(
                name: "Data",
                table: "Items",
                type: "jsonb",
                maxLength: 1024,
                nullable: true);
        }
    }
}
