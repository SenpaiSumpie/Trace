using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Hand-written. Makes completed index snapshots and their chunks immutable in the database,
    /// so a citation into a completed snapshot can never silently change meaning.
    /// Deleting a snapshot (and, by cascade, its chunks) stays possible for retention.
    /// </summary>
    public partial class SnapshotImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION trace_reject_completed_snapshot_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status = 'completed' THEN
                        RAISE EXCEPTION 'index snapshot % is completed and cannot be modified', OLD.id
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'index_snapshots_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER index_snapshots_immutable
                    BEFORE UPDATE ON index_snapshots
                    FOR EACH ROW EXECUTE FUNCTION trace_reject_completed_snapshot_update();

                -- FOR SHARE blocks against a concurrent publish of the snapshot, so a chunk write
                -- either commits before the snapshot completes or sees it completed and fails.
                -- During a cascading snapshot delete the parent row is already gone, so the
                -- chunk deletes pass.
                CREATE FUNCTION trace_reject_completed_snapshot_chunk_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    snapshot_status text;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        SELECT status INTO snapshot_status
                        FROM index_snapshots WHERE id = OLD.snapshot_id FOR SHARE;
                        IF snapshot_status = 'completed' THEN
                            RAISE EXCEPTION 'index snapshot % is completed; its chunks cannot be modified', OLD.snapshot_id
                                USING ERRCODE = 'check_violation', CONSTRAINT = 'source_chunks_snapshot_immutable';
                        END IF;
                    END IF;

                    IF TG_OP <> 'DELETE' THEN
                        SELECT status INTO snapshot_status
                        FROM index_snapshots WHERE id = NEW.snapshot_id FOR SHARE;
                        IF snapshot_status = 'completed' THEN
                            RAISE EXCEPTION 'index snapshot % is completed; chunks cannot be added to it', NEW.snapshot_id
                                USING ERRCODE = 'check_violation', CONSTRAINT = 'source_chunks_snapshot_immutable';
                        END IF;
                        RETURN NEW;
                    END IF;

                    RETURN OLD;
                END;
                $$;

                CREATE TRIGGER source_chunks_snapshot_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON source_chunks
                    FOR EACH ROW EXECUTE FUNCTION trace_reject_completed_snapshot_chunk_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER source_chunks_snapshot_immutable ON source_chunks;
                DROP FUNCTION trace_reject_completed_snapshot_chunk_change();
                DROP TRIGGER index_snapshots_immutable ON index_snapshots;
                DROP FUNCTION trace_reject_completed_snapshot_update();
                """);
        }
    }
}
