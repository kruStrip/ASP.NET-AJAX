using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuzLoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublicBookingForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrivacyConsentAt",
                table: "Bookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    QrStorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    QrVersion = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSettings", x => x.Id);
                    table.CheckConstraint("CK_PaymentSettings_Singleton", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentSettings");

            migrationBuilder.DropColumn(
                name: "PrivacyConsentAt",
                table: "Bookings");
        }
    }
}
