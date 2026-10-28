using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuzLoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookingOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "Bookings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OwnerUserId",
                table: "Bookings",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_AspNetUsers_OwnerUserId",
                table: "Bookings",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_AspNetUsers_OwnerUserId",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_OwnerUserId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Bookings");
        }
    }
}
