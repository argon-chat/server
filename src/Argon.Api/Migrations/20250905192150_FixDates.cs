using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixDates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"Coupons\" ALTER COLUMN \"ValidTo\" TYPE bigint USING EXTRACT(EPOCH FROM \"ValidTo\")::bigint;"
            );
            migrationBuilder.Sql(
                "ALTER TABLE \"Coupons\" ALTER COLUMN \"ValidFrom\" TYPE bigint USING EXTRACT(EPOCH FROM \"ValidFrom\")::bigint;"
            );
            migrationBuilder.Sql(
                "ALTER TABLE \"CouponRedemption\" ALTER COLUMN \"RedeemedAt\" TYPE bigint USING EXTRACT(EPOCH FROM \"RedeemedAt\")::bigint;"
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"Coupons\" ALTER COLUMN \"ValidTo\" TYPE timestamp with time zone USING to_timestamp(\"ValidTo\");"
            );
            migrationBuilder.Sql(
                "ALTER TABLE \"Coupons\" ALTER COLUMN \"ValidFrom\" TYPE timestamp with time zone USING to_timestamp(\"ValidFrom\");"
            );
            migrationBuilder.Sql(
                "ALTER TABLE \"CouponRedemption\" ALTER COLUMN \"RedeemedAt\" TYPE timestamp with time zone USING to_timestamp(\"RedeemedAt\");"
            );
        }
    }
}
