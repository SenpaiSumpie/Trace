using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tenant_scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_cancel_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "repositories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    default_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    github_repo_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repositories", x => x.id);
                    table.CheckConstraint("ck_repositories_visibility", "visibility IN ('public', 'private')");
                });

            migrationBuilder.CreateTable(
                name: "index_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_sha = table.Column<string>(type: "character(40)", fixedLength: true, maxLength: 40, nullable: false),
                    branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    parser_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_index_snapshots", x => x.id);
                    table.UniqueConstraint("ak_index_snapshots_id_repository_id", x => new { x.id, x.repository_id });
                    table.CheckConstraint("ck_index_snapshots_commit_sha", "commit_sha ~ '^[0-9a-f]{40}$'");
                    table.CheckConstraint("ck_index_snapshots_completed_at", "(status = 'completed') = (completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_index_snapshots_status", "status IN ('building', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "fk_index_snapshots_repositories_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "investigations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_investigations", x => x.id);
                    table.UniqueConstraint("ak_investigations_id_snapshot_id", x => new { x.id, x.snapshot_id });
                    table.CheckConstraint("ck_investigations_state", "state IN ('queued', 'running', 'completed', 'failed', 'cancelled')");
                    table.CheckConstraint("ck_investigations_type", "type IN ('question', 'issue_to_plan')");
                    table.ForeignKey(
                        name: "fk_investigations_index_snapshots_snapshot_id_repository_id",
                        columns: x => new { x.snapshot_id, x.repository_id },
                        principalTable: "index_snapshots",
                        principalColumns: new[] { "id", "repository_id" });
                    table.ForeignKey(
                        name: "fk_investigations_repositories_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    language = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    start_line = table.Column<int>(type: "integer", nullable: false),
                    end_line = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_chunks", x => x.id);
                    table.UniqueConstraint("ak_source_chunks_id_snapshot_id", x => new { x.id, x.snapshot_id });
                    table.CheckConstraint("ck_source_chunks_content_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_source_chunks_line_range", "start_line >= 1 AND end_line >= start_line");
                    table.CheckConstraint("ck_source_chunks_text_length", "char_length(text) BETWEEN 1 AND 16000");
                    table.ForeignKey(
                        name: "fk_source_chunks_index_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "index_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence_refs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    locator_json = table.Column<string>(type: "jsonb", nullable: false),
                    excerpt_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_refs", x => x.id);
                    table.CheckConstraint("ck_evidence_refs_code_has_chunk", "source_kind <> 'code' OR chunk_id IS NOT NULL");
                    table.CheckConstraint("ck_evidence_refs_excerpt_hash", "excerpt_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_evidence_refs_source_kind", "source_kind IN ('code', 'issue', 'commit')");
                    table.ForeignKey(
                        name: "fk_evidence_refs_investigations_investigation_id_snapshot_id",
                        columns: x => new { x.investigation_id, x.snapshot_id },
                        principalTable: "investigations",
                        principalColumns: new[] { "id", "snapshot_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_evidence_refs_source_chunks_chunk_id_snapshot_id",
                        columns: x => new { x.chunk_id, x.snapshot_id },
                        principalTable: "source_chunks",
                        principalColumns: new[] { "id", "snapshot_id" });
                });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_refs_chunk_id_snapshot_id",
                table: "evidence_refs",
                columns: new[] { "chunk_id", "snapshot_id" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_refs_investigation_id_snapshot_id",
                table: "evidence_refs",
                columns: new[] { "investigation_id", "snapshot_id" });

            migrationBuilder.CreateIndex(
                name: "ux_index_snapshots_active_key",
                table: "index_snapshots",
                columns: new[] { "repository_id", "commit_sha", "parser_version", "embedding_model" },
                unique: true,
                filter: "status <> 'failed'");

            migrationBuilder.CreateIndex(
                name: "ix_investigations_repository_id",
                table: "investigations",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_investigations_snapshot_id_repository_id",
                table: "investigations",
                columns: new[] { "snapshot_id", "repository_id" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_kind_dedupe_key",
                table: "jobs",
                columns: new[] { "kind", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_created_at",
                table: "jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_repositories_github_repo_id",
                table: "repositories",
                column: "github_repo_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_chunks_snapshot_id_path_start_line_end_line",
                table: "source_chunks",
                columns: new[] { "snapshot_id", "path", "start_line", "end_line" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evidence_refs");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "investigations");

            migrationBuilder.DropTable(
                name: "source_chunks");

            migrationBuilder.DropTable(
                name: "index_snapshots");

            migrationBuilder.DropTable(
                name: "repositories");
        }
    }
}
