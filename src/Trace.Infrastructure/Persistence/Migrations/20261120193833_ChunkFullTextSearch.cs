using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trace.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Hand-written. Adds lexical search over chunks: a stored, generated <c>tsvector</c> built
    /// from the path (weight A) and the text (weight B), with a GIN index. The column is not part
    /// of the EF Core model; it is only used from SQL in <c>EfSnapshotReader</c>.
    /// </summary>
    public partial class ChunkFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- Makes source code searchable as words: splits camelCase and PascalCase
                -- ("PageSize" -> "Page Size", "HTTPServer" -> "HTTP Server"), then turns every
                -- other non-alphanumeric character into a space so that "request.PageSize" or a
                -- file path is not swallowed as one host/file token by the text search parser.
                -- Queries are passed through the same function, so both sides agree.
                -- IMMUTABLE is required for the generated column: changing this function's
                -- behaviour needs a migration that also rewrites the column.
                CREATE FUNCTION trace_fts_normalize(input text) RETURNS text
                LANGUAGE sql IMMUTABLE PARALLEL SAFE RETURNS NULL ON NULL INPUT
                RETURN regexp_replace(
                    regexp_replace(
                        regexp_replace(input, '([a-z0-9])([A-Z])', '\1 \2', 'g'),
                        '([A-Z]+)([A-Z][a-z])', '\1 \2', 'g'),
                    '[^[:alnum:]]+', ' ', 'g');

                ALTER TABLE source_chunks ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        setweight(to_tsvector('english'::regconfig, trace_fts_normalize(path)), 'A') ||
                        setweight(to_tsvector('english'::regconfig, trace_fts_normalize(text)), 'B')
                    ) STORED;

                CREATE INDEX ix_source_chunks_search_vector ON source_chunks USING gin (search_vector);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX ix_source_chunks_search_vector;
                ALTER TABLE source_chunks DROP COLUMN search_vector;
                DROP FUNCTION trace_fts_normalize(text);
                """);
        }
    }
}
