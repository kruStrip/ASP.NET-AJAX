using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuzLoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffBookingTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutgoingMessages_BookingId_Channel_Kind",
                table: "OutgoingMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_BookingId_Channel_Kind",
                table: "OutgoingMessages",
                columns: new[] { "BookingId", "Channel", "Kind" },
                unique: true,
                filter: "\"Kind\" NOT IN (6, 7)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutgoingMessages_BookingId_Channel_Kind",
                table: "OutgoingMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_BookingId_Channel_Kind",
                table: "OutgoingMessages",
                columns: new[] { "BookingId", "Channel", "Kind" },
                unique: true);
        }
    }
}
