using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuzLoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemporaryCustomerAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTemporaryCustomer",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTemporaryCustomer",
                table: "AspNetUsers");
        }
    }
}
