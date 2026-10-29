using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuzLoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaidQuantityChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentId",
                table: "Refunds");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Refunds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "QuantityChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldQuantity = table.Column<int>(type: "integer", nullable: false),
                    NewQuantity = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<string>(type: "text", nullable: true),
                    RefundId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuantityChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuantityChanges_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuantityChanges_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId_Kind",
                table: "Refunds",
                columns: new[] { "PaymentId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId_OpenCancellation",
                table: "Refunds",
                column: "PaymentId",
                unique: true,
                filter: "\"Status\" IN (0, 1) AND \"Kind\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_QuantityChanges_BookingId_Awaiting",
                table: "QuantityChanges",
                column: "BookingId",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_QuantityChanges_RefundId",
                table: "QuantityChanges",
                column: "RefundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuantityChanges");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentId_Kind",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentId_OpenCancellation",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Refunds");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId",
                table: "Refunds",
                column: "PaymentId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }
    }
}
