using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                table: "jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "trace_parent",
                table: "jobs");
        }
    }
}
