using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskFlowApi.Data;

#nullable disable

namespace TaskFlowApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261005120000_AddSecurityOwnership")]
public partial class AddSecurityOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Role",
            table: "Users",
            type: "TEXT",
            maxLength: 20,
            nullable: false,
            defaultValue: "User");

        migrationBuilder.AddColumn<int>(
            name: "CreatedByUserId",
            table: "Tasks",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Tasks_CreatedByUserId",
            table: "Tasks",
            column: "CreatedByUserId");

        migrationBuilder.Sql("UPDATE Users SET Role = 'Admin' WHERE Id = 1;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Tasks_CreatedByUserId", table: "Tasks");
        migrationBuilder.DropColumn(name: "CreatedByUserId", table: "Tasks");
        migrationBuilder.DropColumn(name: "Role", table: "Users");
    }
}
