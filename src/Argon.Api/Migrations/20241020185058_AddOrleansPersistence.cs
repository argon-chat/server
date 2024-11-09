using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrleansPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sql = System.IO.File.ReadAllText("Migrations/orleans_up.sql");
            migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var sql = System.IO.File.ReadAllText("Migrations/orleans_down.sql");
            migrationBuilder.Sql(sql);
        }
    }
}