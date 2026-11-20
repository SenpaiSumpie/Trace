using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trace.Domain;
using Trace.Infrastructure.Persistence;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// The database is the last line of defence: these tests bypass the domain model with raw SQL
/// and assert that PostgreSQL itself rejects rows that would break an invariant.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchemaInvariantTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string ForeignKeyViolation = PostgresErrorCodes.ForeignKeyViolation;
    private const string CheckViolation = PostgresErrorCodes.CheckViolation;
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;

    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    private TestDatabase database = null!;
    private TraceDbContext db = null!;

    public async Task InitializeAsync()
    {
        database = await postgres.CreateDatabaseAsync();
        db = database.CreateContext();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task Snapshot_requires_an_existing_repository()
    {
        await AssertRejectedAsync(
            () => InsertSnapshotAsync(Guid.NewGuid(), TestGraph.ShaA, "building", completed: false),
            ForeignKeyViolation,
            "fk_index_snapshots_repositories_repository_id");
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("main")]
    public async Task Snapshot_requires_a_full_lowercase_commit_sha(string commitSha)
    {
        var repository = await TestGraph.AddRepositoryAsync(db);

        await AssertRejectedAsync(
            () => InsertSnapshotAsync(repository.Id, commitSha, "building", completed: false),
            CheckViolation,
            "ck_index_snapshots_commit_sha");
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("building", true)]
    [InlineData("failed", true)]
    public async Task Snapshot_has_a_completion_time_exactly_when_completed(string status, bool completed)
    {
        var repository = await TestGraph.AddRepositoryAsync(db);

        await AssertRejectedAsync(
            () => InsertSnapshotAsync(repository.Id, TestGraph.ShaA, status, completed),
            CheckViolation,
            "ck_index_snapshots_completed_at");
    }

    [Fact]
    public async Task Snapshot_status_must_be_known()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);

        await AssertRejectedAsync(
            () => InsertSnapshotAsync(repository.Id, TestGraph.ShaA, "published", completed: false),
            CheckViolation,
            "ck_index_snapshots_status");
    }

    [Fact]
    public async Task Indexing_the_same_commit_twice_is_rejected_until_the_first_attempt_fails()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var first = TestGraph.NewSnapshot(repository);
        db.Add(first);
        await db.SaveChangesAsync();

        await using (var other = database.CreateContext())
        {
            other.Add(TestGraph.NewSnapshot(repository));
            await AssertRejectedAsync(() => other.SaveChangesAsync(), UniqueViolation, "ux_index_snapshots_active_key");
        }

        first.Fail();
        await db.SaveChangesAsync();

        db.Add(TestGraph.NewSnapshot(repository));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.IndexSnapshots.CountAsync());
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 4)]
    public async Task Chunk_requires_a_valid_line_range(int startLine, int endLine)
    {
        var (snapshot, _) = await TestGraph.AddBuildingSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await AssertRejectedAsync(
            () => InsertChunkAsync(snapshot.Id, startLine, endLine, "text"),
            CheckViolation,
            "ck_source_chunks_line_range");
    }

    [Fact]
    public async Task Chunk_text_is_capped()
    {
        var (snapshot, _) = await TestGraph.AddBuildingSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await AssertRejectedAsync(
            () => InsertChunkAsync(snapshot.Id, 10, 20, new string('x', SourceChunk.MaxTextLength + 1)),
            CheckViolation,
            "ck_source_chunks_text_length");
    }

    [Fact]
    public async Task Chunks_of_a_building_snapshot_can_change()
    {
        var (snapshot, chunk) = await TestGraph.AddBuildingSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await InsertChunkAsync(snapshot.Id, 10, 20, "text");
        var deleted = await db.Database.ExecuteSqlAsync($"DELETE FROM source_chunks WHERE id = {chunk.Id}");

        Assert.Equal(1, deleted);
    }

    [Fact]
    public async Task Completed_snapshot_cannot_be_modified()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync(
                $"UPDATE index_snapshots SET status = 'building', completed_at = NULL WHERE id = {snapshot.Id}"),
            CheckViolation,
            "index_snapshots_immutable");
    }

    [Fact]
    public async Task Chunks_cannot_be_added_to_a_completed_snapshot()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await AssertRejectedAsync(
            () => InsertChunkAsync(snapshot.Id, 10, 20, "text"),
            CheckViolation,
            "source_chunks_snapshot_immutable");
    }

    [Fact]
    public async Task Chunks_of_a_completed_snapshot_cannot_be_updated()
    {
        var (_, chunk) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"UPDATE source_chunks SET text = 'tampered' WHERE id = {chunk.Id}"),
            CheckViolation,
            "source_chunks_snapshot_immutable");
    }

    [Fact]
    public async Task Chunks_of_a_completed_snapshot_cannot_be_deleted()
    {
        var (_, chunk) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"DELETE FROM source_chunks WHERE id = {chunk.Id}"),
            CheckViolation,
            "source_chunks_snapshot_immutable");
    }

    [Fact]
    public async Task Chunk_cannot_be_moved_into_a_completed_snapshot()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var (completed, _) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaA);
        var (_, movable) = await TestGraph.AddBuildingSnapshotAsync(db, repository, TestGraph.ShaB);

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync(
                $"UPDATE source_chunks SET snapshot_id = {completed.Id}, path = 'moved.cs' WHERE id = {movable.Id}"),
            CheckViolation,
            "source_chunks_snapshot_immutable");
    }

    [Fact]
    public async Task Chunk_write_racing_a_publish_waits_and_is_rejected_once_the_publish_commits()
    {
        var (snapshot, _) = await TestGraph.AddBuildingSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));

        await using var publisher = database.CreateContext();
        await using var publish = await publisher.Database.BeginTransactionAsync();
        await publisher.Database.ExecuteSqlAsync(
            $"UPDATE index_snapshots SET status = 'completed', completed_at = now() WHERE id = {snapshot.Id}");

        var racingInsert = InsertChunkAsync(snapshot.Id, 10, 20, "text");
        var finishedFirst = await Task.WhenAny(racingInsert, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(racingInsert, finishedFirst);

        await publish.CommitAsync();

        await AssertRejectedAsync(() => racingInsert, CheckViolation, "source_chunks_snapshot_immutable");
    }

    [Fact]
    public async Task Investigation_cannot_pin_a_snapshot_from_another_repository()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var otherRepository = await TestGraph.AddRepositoryAsync(db, "other-repo");

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"""
                INSERT INTO investigations (id, repository_id, snapshot_id, type, question, state, created_at, updated_at)
                VALUES ({Guid.NewGuid()}, {otherRepository.Id}, {snapshot.Id}, 'question', 'q', 'queued', now(), now())
                """),
            ForeignKeyViolation,
            "fk_investigations_index_snapshots_snapshot_id_repository_id");
    }

    [Fact]
    public async Task Evidence_cannot_cite_a_chunk_from_another_snapshot()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var (pinned, _) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaA);
        var (_, foreignChunk) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaB);
        var investigation = await TestGraph.AddInvestigationAsync(db, pinned);

        await AssertRejectedAsync(
            () => InsertEvidenceAsync(investigation.Id, pinned.Id, foreignChunk.Id, "code"),
            ForeignKeyViolation,
            "fk_evidence_refs_source_chunks_chunk_id_snapshot_id");
    }

    [Fact]
    public async Task Evidence_cannot_claim_a_snapshot_other_than_the_pinned_one()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var (pinned, _) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaA);
        var (other, otherChunk) = await TestGraph.AddCompletedSnapshotAsync(db, repository, TestGraph.ShaB);
        var investigation = await TestGraph.AddInvestigationAsync(db, pinned);

        await AssertRejectedAsync(
            () => InsertEvidenceAsync(investigation.Id, other.Id, otherChunk.Id, "code"),
            ForeignKeyViolation,
            "fk_evidence_refs_investigations_investigation_id_snapshot_id");
    }

    [Fact]
    public async Task Code_evidence_requires_a_chunk()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);

        await AssertRejectedAsync(
            () => InsertEvidenceAsync(investigation.Id, snapshot.Id, chunkId: null, "code"),
            CheckViolation,
            "ck_evidence_refs_code_has_chunk");
    }

    [Fact]
    public async Task Evidence_from_the_pinned_snapshot_is_accepted()
    {
        var (snapshot, chunk) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);

        db.Add(EvidenceRef.ForCode(investigation, chunk, """{"path":"src/App.cs","startLine":1,"endLine":2}""", "line one\nline two"));
        await db.SaveChangesAsync();
        await InsertEvidenceAsync(investigation.Id, snapshot.Id, chunkId: null, "issue");

        Assert.Equal(2, await db.EvidenceRefs.CountAsync());
    }

    [Fact]
    public async Task Snapshot_cited_by_an_investigation_cannot_be_deleted_on_its_own()
    {
        var (snapshot, _) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        await TestGraph.AddInvestigationAsync(db, snapshot);

        await AssertRejectedAsync(
            () => db.Database.ExecuteSqlAsync($"DELETE FROM index_snapshots WHERE id = {snapshot.Id}"),
            ForeignKeyViolation,
            "fk_investigations_index_snapshots_snapshot_id_repository_id");
    }

    [Fact]
    public async Task Deleting_a_repository_removes_all_of_its_data()
    {
        var repository = await TestGraph.AddRepositoryAsync(db);
        var (snapshot, chunk) = await TestGraph.AddCompletedSnapshotAsync(db, repository);
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);
        db.Add(EvidenceRef.ForCode(investigation, chunk, """{"path":"src/App.cs"}""", "line one"));
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"DELETE FROM repositories WHERE id = {repository.Id}");

        Assert.Equal(0, await db.IndexSnapshots.CountAsync());
        Assert.Equal(0, await db.SourceChunks.CountAsync());
        Assert.Equal(0, await db.Investigations.CountAsync());
        Assert.Equal(0, await db.EvidenceRefs.CountAsync());
    }

    [Fact]
    public async Task Job_dedupe_key_is_unique_per_kind()
    {
        db.Add(Job.Enqueue("index", "repository:1", "delivery-1", "{}"));
        db.Add(Job.Enqueue("investigate", "repository:1", "delivery-1", "{}"));
        await db.SaveChangesAsync();

        await using var other = database.CreateContext();
        other.Add(Job.Enqueue("index", "repository:1", "delivery-1", "{}"));

        await AssertRejectedAsync(() => other.SaveChangesAsync(), UniqueViolation, "ix_jobs_kind_dedupe_key");
    }

    [Fact]
    public async Task Entities_round_trip_with_readable_enums_and_utc_timestamps()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (snapshot, chunk) = await TestGraph.AddCompletedSnapshotAsync(db, await TestGraph.AddRepositoryAsync(db));
        var investigation = await TestGraph.AddInvestigationAsync(db, snapshot);

        await using var fresh = database.CreateContext();
        var loadedSnapshot = await fresh.IndexSnapshots.SingleAsync();
        var loadedChunk = await fresh.SourceChunks.SingleAsync();
        var loadedInvestigation = await fresh.Investigations.SingleAsync();
        var storedType = await fresh.Database
            .SqlQuery<string>($"SELECT type AS \"Value\" FROM investigations WHERE id = {investigation.Id}")
            .SingleAsync();

        Assert.Equal(SnapshotStatus.Completed, loadedSnapshot.Status);
        Assert.Equal(TestGraph.ShaA, loadedSnapshot.CommitSha);
        Assert.Equal(TimeSpan.Zero, loadedSnapshot.CreatedAt.Offset);
        Assert.InRange(loadedSnapshot.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(loadedSnapshot.UpdatedAt >= loadedSnapshot.CreatedAt);
        Assert.Equal(chunk.ContentHash, loadedChunk.ContentHash);
        Assert.Equal(chunk.Text, loadedChunk.Text);
        Assert.Equal(InvestigationType.IssueToPlan, loadedInvestigation.Type);
        Assert.Equal("issue_to_plan", storedType);
    }

    private static async Task AssertRejectedAsync(Func<Task> act, string sqlState, string constraintName)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(act);
        var postgresException = thrown as PostgresException ?? thrown.InnerException as PostgresException;

        Assert.NotNull(postgresException);
        Assert.Equal(sqlState, postgresException.SqlState);
        Assert.Equal(constraintName, postgresException.ConstraintName);
    }

    private Task InsertSnapshotAsync(Guid repositoryId, string commitSha, string status, bool completed)
    {
        DateTimeOffset? completedAt = completed ? DateTimeOffset.UtcNow : null;
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO index_snapshots
                (id, repository_id, commit_sha, branch, parser_version, embedding_model, status, completed_at, created_at, updated_at)
            VALUES
                ({Guid.NewGuid()}, {repositoryId}, {commitSha}, 'main', 'text-v1', 'fake-v1', {status}, {completedAt}, now(), now())
            """);
    }

    private Task InsertChunkAsync(Guid snapshotId, int startLine, int endLine, string text) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO source_chunks
                (id, snapshot_id, path, language, start_line, end_line, content_hash, text, created_at, updated_at)
            VALUES
                ({Guid.NewGuid()}, {snapshotId}, 'src/Other.cs', 'csharp', {startLine}, {endLine}, {Hash}, {text}, now(), now())
            """);

    private Task InsertEvidenceAsync(Guid investigationId, Guid snapshotId, Guid? chunkId, string sourceKind) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO evidence_refs
                (id, investigation_id, snapshot_id, chunk_id, source_kind, locator_json, excerpt_hash, created_at, updated_at)
            VALUES
                ({Guid.NewGuid()}, {investigationId}, {snapshotId}, {chunkId}, {sourceKind}, {"{}"}::jsonb, {Hash}, now(), now())
            """);
}
