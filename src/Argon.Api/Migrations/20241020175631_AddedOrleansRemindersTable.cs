using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddedOrleansRemindersTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orleansreminderstable",
                columns: table => new
                {
                    serviceid = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    grainid = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    remindername = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    starttime = table.Column<DateTime>(type: "timestamp(3) with time zone", precision: 3, nullable: false),
                    period = table.Column<long>(type: "bigint", nullable: false),
                    grainhash = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminderstable_serviceid_grainid_remindername", x => new { x.serviceid, x.grainid, x.remindername });
                });

            migrationBuilder.CreateTable(
                name: "orleansstorage",
                columns: table => new
                {
                    grainidhash = table.Column<int>(type: "integer", nullable: false),
                    grainidn0 = table.Column<long>(type: "bigint", nullable: false),
                    grainidn1 = table.Column<long>(type: "bigint", nullable: false),
                    graintypehash = table.Column<int>(type: "integer", nullable: false),
                    graintypestring = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    grainidextensionstring = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    serviceid = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    payloadbinary = table.Column<byte[]>(type: "bytea", nullable: true),
                    modifiedon = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateIndex(
                name: "ix_orleansstorage",
                table: "orleansstorage",
                columns: new[] { "grainidhash", "graintypehash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orleansreminderstable");

            migrationBuilder.DropTable(
                name: "orleansstorage");
        }
    }
}
