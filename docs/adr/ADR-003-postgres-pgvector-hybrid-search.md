# ADR-003: Hybrid search in PostgreSQL — full-text + exact vector search, fused by rank

Status: Accepted · 2026-11-20

## Context

Investigations need the most relevant chunks of one pinned snapshot for a natural-language
question or an identifier. Lexical search finds exact names; embeddings find paraphrases.
The handoff asks for both, merged, without new infrastructure (Sections 3 and 6).

## Options

1. **Vector search only.** Misses exact identifiers and is only as good as the embedding model.
2. **Full-text search only.** Misses questions phrased differently from the code.
3. **Both in PostgreSQL, fused by reciprocal rank.** One store, one transaction boundary.
4. **A dedicated search engine or vector store.** More capable at scale, but a second system
   to secure, deploy and keep consistent with immutable snapshots.

## Decision

Option 3.

- **Lexical.** `source_chunks.search_vector` is a stored, generated `tsvector` (`english`
  configuration, path weighted above text) with a GIN index. A SQL function,
  `trace_fts_normalize`, splits camelCase/PascalCase and replaces punctuation with spaces on
  both the indexed text and the query, so `MaxPageSize`, `max_page_size` and "max page size"
  all match, and `request.PageSize` is not swallowed as one token. Query words are OR-ed and
  ordered by `ts_rank_cd`, because a question rarely has every word in one chunk.
- **Vector.** Cosine distance (`<=>`) over `embedding`, as an **exact** scan filtered by
  `snapshot_id`. There is deliberately **no approximate (HNSW/IVFFlat) index**: every query is
  confined to one snapshot, and an approximate index combined with that filter can silently
  return fewer or worse neighbours. Exact search has perfect recall and is fast at the size of
  one repository snapshot.
- **Fusion.** Reciprocal rank fusion (K = 60) over the top 50 of each list; ties broken by path
  and line so results are deterministic. Only ranks are used, so the two scores need no
  calibration.
- **Scope.** Every chunk query takes a snapshot ID; the tools resolve that snapshot through its
  repository and only read completed snapshots. A snapshot indexed with a different embedding
  model than the configured provider is not searched.
- The column, function and index are hand-written SQL in a migration and are not part of the
  EF Core model; they are used only from `EfSnapshotReader`.

## Consequences

- No extra service to run. Search results are always consistent with the immutable snapshot.
- Vector query cost grows linearly with the number of chunks in a snapshot.
- `trace_fts_normalize` is `IMMUTABLE` because a generated column depends on it. Changing its
  behaviour requires a migration that rewrites the column.
- The `english` configuration stems and drops stop words, which suits questions and comments
  but is not language-aware for code; identifiers are handled only by the normalizer.
- Ranking has not been tuned. The measured baseline on the labeled fixture questions is
  recorded in `docs/open-questions.md`.

## Revisit trigger

- Snapshots large enough that exact vector search dominates query latency (measure first) —
  then add an approximate index, using pgvector's iterative scans or per-snapshot partitioning
  so the snapshot filter stays correct.
- Evaluation with a real embedding model showing that equal-weight fusion or `ts_rank_cd`
  ordering is the limiting factor — then tune weights or add a reranker.
- Measured query or index limits in PostgreSQL — then consider a dedicated search backend.
