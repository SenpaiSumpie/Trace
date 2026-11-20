using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvestigationWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_investigations_repository_id",
                table: "investigations");

            migrationBuilder.AddColumn<string>(
                name: "error_code",
                table: "investigations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "investigations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_id",
                table: "investigations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_version",
                table: "investigations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "investigation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    tool_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_investigation_events", x => x.id);
                    table.CheckConstraint("ck_investigation_events_event_type", "event_type IN ('started', 'tool_called', 'tool_failed', 'tool_rejected', 'citation_rejected', 'completed', 'failed')");
                    table.CheckConstraint("ck_investigation_events_seq", "seq >= 1");
                    table.ForeignKey(
                        name: "fk_investigation_events_investigations_investigation_id",
                        column: x => x.investigation_id,
                        principalTable: "investigations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_investigations_idempotency_key",
                table: "investigations",
                columns: new[] { "repository_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_investigation_events_investigation_id_seq",
                table: "investigation_events",
                columns: new[] { "investigation_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "investigation_events");

            migrationBuilder.DropIndex(
                name: "ux_investigations_idempotency_key",
                table: "investigations");

            migrationBuilder.DropColumn(
                name: "error_code",
                table: "investigations");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "investigations");

            migrationBuilder.DropColumn(
                name: "model_id",
                table: "investigations");

            migrationBuilder.DropColumn(
                name: "prompt_version",
                table: "investigations");

            migrationBuilder.CreateIndex(
                name: "ix_investigations_repository_id",
                table: "investigations",
                column: "repository_id");
        }
    }
}
