using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The table and its constraints are generated; the triggers below them are hand-written.
    /// They hold what a table definition cannot (ADR-016): only an issue-to-plan investigation
    /// has a plan; a finished plan changes only by being reviewed, once; a plan gets a result
    /// only when its investigation completed; every citation in a result is a code evidence row
    /// of the same investigation, and that evidence then stays as it is.
    /// </summary>
    public partial class Plans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_investigations_id_snapshot_id_repository_id",
                table: "investigations",
                columns: new[] { "id", "snapshot_id", "repository_id" });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_number = table.Column<int>(type: "integer", nullable: false),
                    issue_title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    issue_content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                    table.CheckConstraint("ck_plans_issue_content_hash", "issue_content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_plans_issue_number", "issue_number >= 1");
                    table.CheckConstraint("ck_plans_issue_recorded", "(issue_title IS NULL) = (issue_content_hash IS NULL)");
                    table.CheckConstraint("ck_plans_issue_title_single_line", "issue_title !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_plans_result_has_issue", "result_json IS NULL OR issue_content_hash IS NOT NULL");
                    table.CheckConstraint("ck_plans_result_has_schema_version", "(result_json IS NULL) = (schema_version IS NULL)");
                    table.CheckConstraint("ck_plans_result_schema_version_matches", "result_json IS NULL OR (jsonb_typeof(result_json) = 'object' AND (result_json ->> 'schemaVersion') IS NOT DISTINCT FROM schema_version)");
                    table.CheckConstraint("ck_plans_reviewed_is_finished", "reviewed_at IS NULL OR result_json IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_plans_investigations",
                        columns: x => new { x.investigation_id, x.snapshot_id, x.repository_id },
                        principalTable: "investigations",
                        principalColumns: new[] { "id", "snapshot_id", "repository_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_plans_investigation",
                table: "plans",
                columns: new[] { "investigation_id", "snapshot_id", "repository_id" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION trace_guard_plan_insert() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    investigation_type text;
                BEGIN
                    SELECT type INTO investigation_type FROM investigations WHERE id = NEW.investigation_id;
                    IF investigation_type <> 'issue_to_plan' THEN
                        RAISE EXCEPTION 'investigation % is not an issue-to-plan investigation; it cannot have a plan', NEW.investigation_id
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'plans_investigation_type';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER plans_investigation_type
                    BEFORE INSERT ON plans
                    FOR EACH ROW EXECUTE FUNCTION trace_guard_plan_insert();

                -- A plan never moves to another investigation or issue. Once it has a result,
                -- the whole row is frozen except reviewed_at (and the updated_at bookkeeping),
                -- and reviewed_at can be set once. Comparing the rows as JSON means a column
                -- added later is frozen too, without touching this function.
                CREATE FUNCTION trace_guard_plan_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.id <> OLD.id
                        OR NEW.investigation_id <> OLD.investigation_id
                        OR NEW.snapshot_id <> OLD.snapshot_id
                        OR NEW.repository_id <> OLD.repository_id
                        OR NEW.issue_number <> OLD.issue_number
                        OR NEW.created_at <> OLD.created_at THEN
                        RAISE EXCEPTION 'plan % cannot be moved to another investigation or issue', OLD.id
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'plans_identity_immutable';
                    END IF;

                    IF OLD.result_json IS NOT NULL THEN
                        IF (to_jsonb(NEW) - 'reviewed_at' - 'updated_at')
                            IS DISTINCT FROM (to_jsonb(OLD) - 'reviewed_at' - 'updated_at') THEN
                            RAISE EXCEPTION 'plan % is finished; only its review can be recorded', OLD.id
                                USING ERRCODE = 'check_violation', CONSTRAINT = 'plans_finished_immutable';
                        END IF;

                        IF OLD.reviewed_at IS NOT NULL AND NEW.reviewed_at IS DISTINCT FROM OLD.reviewed_at THEN
                            RAISE EXCEPTION 'plan % was already reviewed; the first review stands', OLD.id
                                USING ERRCODE = 'check_violation', CONSTRAINT = 'plans_reviewed_once';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER plans_finished_immutable
                    BEFORE UPDATE ON plans
                    FOR EACH ROW EXECUTE FUNCTION trace_guard_plan_update();

                -- Runs when the transaction commits, because the investigation's completion, its
                -- evidence rows and the plan's result are written together and in no fixed order.
                -- The citations of a result are the strings under any "citations" key and the
                -- "id" of each "evidence" entry (docs/plan-result-schema.md). Each must be the ID
                -- of a code evidence row of this plan's investigation; the foreign keys of
                -- evidence_refs then tie it to a chunk of the investigation's snapshot.
                CREATE FUNCTION trace_check_plan_result() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    investigation_state text;
                BEGIN
                    -- Deleted later in the same transaction (with its investigation): nothing to check.
                    IF NOT EXISTS (SELECT 1 FROM plans WHERE id = NEW.id) THEN
                        RETURN NULL;
                    END IF;

                    SELECT state INTO investigation_state FROM investigations WHERE id = NEW.investigation_id;
                    IF investigation_state IS DISTINCT FROM 'completed' THEN
                        RAISE EXCEPTION 'plan % has a result but its investigation is not completed', NEW.id
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'plans_result_requires_completed_investigation';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM (
                            SELECT jsonb_path_query(NEW.result_json, '$.**.citations[*]') #>> '{}' AS evidence_id
                            UNION ALL
                            -- An entry without an id gives NULL here, which matches no row.
                            SELECT entry ->> 'id' FROM jsonb_path_query(NEW.result_json, '$.evidence[*]') AS entry
                        ) AS cited
                        WHERE NOT EXISTS (
                            SELECT 1 FROM evidence_refs
                            WHERE evidence_refs.investigation_id = NEW.investigation_id
                              AND evidence_refs.source_kind = 'code'
                              AND evidence_refs.id::text = cited.evidence_id)
                    ) THEN
                        RAISE EXCEPTION 'plan % cites something that is not code evidence of its investigation', NEW.id
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'plans_citations_are_evidence';
                    END IF;

                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER plans_result_checked
                    AFTER INSERT OR UPDATE OF result_json ON plans
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW WHEN (NEW.result_json IS NOT NULL)
                    EXECUTE FUNCTION trace_check_plan_result();

                -- What a finished plan cites must stay what it cited. When the investigation
                -- itself is deleted its row is already gone here, so the cascade passes.
                CREATE FUNCTION trace_reject_planned_evidence_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM plans
                        JOIN investigations ON investigations.id = plans.investigation_id
                        WHERE plans.investigation_id = OLD.investigation_id AND plans.result_json IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'evidence % belongs to a finished plan and cannot be modified', OLD.id
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'evidence_refs_plan_immutable';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER evidence_refs_plan_immutable
                    BEFORE UPDATE OR DELETE ON evidence_refs
                    FOR EACH ROW EXECUTE FUNCTION trace_reject_planned_evidence_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER evidence_refs_plan_immutable ON evidence_refs;
                DROP FUNCTION trace_reject_planned_evidence_change();
                DROP TRIGGER plans_result_checked ON plans;
                DROP FUNCTION trace_check_plan_result();
                DROP TRIGGER plans_finished_immutable ON plans;
                DROP FUNCTION trace_guard_plan_update();
                DROP TRIGGER plans_investigation_type ON plans;
                DROP FUNCTION trace_guard_plan_insert();
                """);

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_investigations_id_snapshot_id_repository_id",
                table: "investigations");
        }
    }
}
