using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddedOrleansPersistencyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orleansmembershipversiontable",
                columns: table => new
                {
                    deploymentid = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp(3) with time zone", precision: 3, nullable: false, defaultValueSql: "now()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orleansmembershipversiontable_deploymentid", x => x.deploymentid);
                });

            migrationBuilder.CreateTable(
                name: "orleansquery",
                columns: table => new
                {
                    querykey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    querytext = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("orleansquery_key", x => x.querykey);
                });

            migrationBuilder.CreateTable(
                name: "orleansmembershiptable",
                columns: table => new
                {
                    deploymentid = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    siloname = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    hostname = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    proxyport = table.Column<int>(type: "integer", nullable: true),
                    suspecttimes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    starttime = table.Column<DateTime>(type: "timestamp(3) with time zone", precision: 3, nullable: false),
                    iamalivetime = table.Column<DateTime>(type: "timestamp(3) with time zone", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membershiptable_deploymentid", x => new { x.deploymentid, x.address, x.port, x.generation });
                    table.ForeignKey(
                        name: "fk_membershiptable_membershipversiontable_deploymentid",
                        column: x => x.deploymentid,
                        principalTable: "orleansmembershipversiontable",
                        principalColumn: "deploymentid");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orleansmembershiptable");

            migrationBuilder.DropTable(
                name: "orleansquery");

            migrationBuilder.DropTable(
                name: "orleansmembershipversiontable");
        }
    }
}
