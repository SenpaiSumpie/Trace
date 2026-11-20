using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_status_created_at",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_attempts",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investigation_events_event_type",
                table: "investigation_events");

            migrationBuilder.RenameIndex(
                name: "ix_jobs_kind_dedupe_key",
                table: "jobs",
                newName: "ux_jobs_kind_dedupe_key");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "available_at",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_attempts",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            // Hand-written: jobs that already exist become claimable from when they were created.
            migrationBuilder.Sql("UPDATE jobs SET available_at = created_at;");

            migrationBuilder.AddColumn<Guid>(
                name: "building_job_id",
                table: "index_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_available_at",
                table: "jobs",
                columns: new[] { "status", "available_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_attempts",
                table: "jobs",
                sql: "attempts >= 0 AND max_attempts >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_lease",
                table: "jobs",
                sql: "(status = 'running') = (lease_until IS NOT NULL AND lease_token IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investigation_events_event_type",
                table: "investigation_events",
                sql: "event_type IN ('started', 'tool_called', 'tool_failed', 'tool_rejected', 'citation_rejected', 'completed', 'failed', 'cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_status_available_at",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_attempts",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_lease",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_investigation_events_event_type",
                table: "investigation_events");

            migrationBuilder.DropColumn(
                name: "available_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "max_attempts",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "building_job_id",
                table: "index_snapshots");

            migrationBuilder.RenameIndex(
                name: "ux_jobs_kind_dedupe_key",
                table: "jobs",
                newName: "ix_jobs_kind_dedupe_key");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_created_at",
                table: "jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_attempts",
                table: "jobs",
                sql: "attempts >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_investigation_events_event_type",
                table: "investigation_events",
                sql: "event_type IN ('started', 'tool_called', 'tool_failed', 'tool_rejected', 'citation_rejected', 'completed', 'failed')");
        }
    }
}
