using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvestigationCacheUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "cache_read_tokens",
                table: "investigations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "cache_write_tokens",
                table: "investigations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cache_read_tokens",
                table: "investigations");

            migrationBuilder.DropColumn(
                name: "cache_write_tokens",
                table: "investigations");
        }
    }
}
