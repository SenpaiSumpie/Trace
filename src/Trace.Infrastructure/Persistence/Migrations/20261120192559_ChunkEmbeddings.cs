using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChunkEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "source_chunks",
                type: "vector(1024)",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "embedding",
                table: "source_chunks");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
