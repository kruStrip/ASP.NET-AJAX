using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuzLoto.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DefaultGameManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Existing games without any manager go to the first Manager account (by creation time).
            migrationBuilder.Sql("""
                INSERT INTO "EventStaffAssignments" ("UserId", "GameSessionId")
                SELECT manager."Id", s."Id"
                FROM "GameSessions" AS s
                CROSS JOIN LATERAL (
                    SELECT u."Id"
                    FROM "AspNetUsers" AS u
                    JOIN "AspNetUserRoles" AS ur ON ur."UserId" = u."Id"
                    JOIN "AspNetRoles" AS r ON r."Id" = ur."RoleId"
                    WHERE r."NormalizedName" = 'MANAGER'
                    ORDER BY u."CreatedAt", u."UserName"
                    LIMIT 1
                ) AS manager
                WHERE NOT EXISTS (
                    SELECT 1 FROM "EventStaffAssignments" AS a
                    WHERE a."GameSessionId" = s."Id"
                )
                ON CONFLICT ("UserId", "GameSessionId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AspNetUsers");
        }
    }
}
