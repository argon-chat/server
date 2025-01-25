using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class OrleansCleanupDefunctSiloEntriesKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO OrleansQuery(QueryKey, QueryText)
                VALUES
                (
                    'CleanupDefunctSiloEntriesKey','
                    DELETE FROM OrleansMembershipTable
                    WHERE DeploymentId = @DeploymentId
                        AND @DeploymentId IS NOT NULL
                        AND IAmAliveTime < @IAmAliveTime
                        AND Status != 3;
                ');         
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
