using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuzLoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutgoingMessages_BookingId_Channel",
                table: "OutgoingMessages");

            migrationBuilder.AddColumn<string>(
                name: "ContentUri",
                table: "OutgoingMessages",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "OutgoingMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Every message queued before this migration was the post-payment confirmation.
            migrationBuilder.Sql("UPDATE \"OutgoingMessages\" SET \"Kind\" = 2;");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_BookingId_Channel_Kind",
                table: "OutgoingMessages",
                columns: new[] { "BookingId", "Channel", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutgoingMessages_BookingId_Channel_Kind",
                table: "OutgoingMessages");

            migrationBuilder.DropColumn(
                name: "ContentUri",
                table: "OutgoingMessages");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "OutgoingMessages");

            // The old unique index allowed one message per channel, so drop the payment-instruction rows first.
            migrationBuilder.Sql("DELETE FROM \"OutgoingMessages\" WHERE \"Kind\" <> 2;");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_BookingId_Channel",
                table: "OutgoingMessages",
                columns: new[] { "BookingId", "Channel" },
                unique: true);
        }
    }
}
