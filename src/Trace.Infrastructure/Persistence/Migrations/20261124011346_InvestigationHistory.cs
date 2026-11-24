using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvestigationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_investigations_user_id",
                table: "investigations");

            migrationBuilder.AddColumn<string>(
                name: "visitor_key",
                table: "investigations",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_investigations_user_history",
                table: "investigations",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true },
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_investigations_visitor_history",
                table: "investigations",
                columns: new[] { "visitor_key", "created_at", "id" },
                descending: new[] { false, true, true },
                filter: "visitor_key IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investigations_one_owner",
                table: "investigations",
                sql: "user_id IS NULL OR visitor_key IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investigations_visitor_key",
                table: "investigations",
                sql: "visitor_key ~ '^[0-9a-f]{64}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_investigations_user_history",
                table: "investigations");

            migrationBuilder.DropIndex(
                name: "ix_investigations_visitor_history",
                table: "investigations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investigations_one_owner",
                table: "investigations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investigations_visitor_key",
                table: "investigations");

            migrationBuilder.DropColumn(
                name: "visitor_key",
                table: "investigations");

            migrationBuilder.CreateIndex(
                name: "ix_investigations_user_id",
                table: "investigations",
                column: "user_id");
        }
    }
}
