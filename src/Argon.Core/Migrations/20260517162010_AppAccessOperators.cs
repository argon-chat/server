using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Core.Migrations
{
    /// <inheritdoc />
    public partial class AppAccessOperators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperatorAppAccess",
                columns: table => new
                {
                    OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedScopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    Claims = table.Column<List<string>>(type: "text[]", nullable: false),
                    GrantedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorAppAccess", x => new { x.OperatorId, x.AppId });
                    table.ForeignKey(
                        name: "FK_OperatorAppAccess_DevApps_AppId",
                        column: x => x.AppId,
                        principalTable: "DevApps",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OperatorAppAccess_Operators_OperatorId",
                        column: x => x.OperatorId,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAppAccess_AppId",
                table: "OperatorAppAccess",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAppAccess_GrantedBy",
                table: "OperatorAppAccess",
                column: "GrantedBy");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorAppAccess_OperatorId",
                table: "OperatorAppAccess",
                column: "OperatorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperatorAppAccess");
        }
    }
}
