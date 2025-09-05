using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argon.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "ValidTo",
                table: "Coupons",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<long>(
                name: "ValidFrom",
                table: "Coupons",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<long>(
                name: "RedeemedAt",
                table: "CouponRedemption",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "ValidTo",
                table: "Coupons",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ValidFrom",
                table: "Coupons",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<DateTime>(
                name: "RedeemedAt",
                table: "CouponRedemption",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");
        }
    }
}
