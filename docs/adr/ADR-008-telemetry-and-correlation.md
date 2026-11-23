# ADR-008: Trace with OpenTelemetry; carry the trace and a correlation ID on the job row

Status: Accepted · 2026-11-23

## Context

A question passes through a browser request, a queued job that a different process may run
minutes later, several model calls and several tool calls. When the first real run failed
(a wrong client secret, then a provider rate limit) the logs could not connect those steps or
say why a provider refused. The handoff (Sections 5 and 11) requires OpenTelemetry tracing,
a correlation ID that flows from web to API to job to model and tool events, and telemetry
that redacts code, secrets and tokens by default.

## Decision

- **One activity source and one meter, both named `Trace`**, declared in
  `Trace.Application/Telemetry` using only `System.Diagnostics`. The application layer has no
  OpenTelemetry dependency; the hosts subscribe and export through `AddTraceTelemetry`.
- **Spans:** incoming requests (API), outgoing HTTP calls (GitHub, Voyage, Anthropic), and
  `job <kind>`, `investigation.run`, `model.turn`, `tool <name>` and `index.snapshot`.
- **The job row carries the trace.** `jobs.trace_parent` (W3C trace context) and
  `jobs.correlation_id` are stamped when the row is inserted, in the one place every enqueue
  path goes through. The worker starts the job span as a child of that context, so one trace
  covers the request, the wait in the queue and the work, across processes.
- **Correlation ID:** the API accepts `X-Correlation-ID` only if it is 1–64 characters of
  `A-Z a-z 0-9 . _ -`; otherwise it generates one. It is returned on every response, added to
  the log scope, carried as baggage through the trace, stored on the job, logged by the
  worker, and written to the investigation's `started` event together with the trace ID.
- **Allow-list, not redaction.** Spans and metrics record identifiers, counts, durations and
  fixed outcome names only. Questions, search queries, tool arguments, source text, model
  output and exception messages are never attached. HTTP instrumentation records no bodies or
  headers. A test asserts that a question and the source it retrieved appear nowhere in the
  spans of its trace.
- **Export is opt-in.** Spans are always created (so log lines always have a trace ID), but
  nothing leaves the process unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
- **Provider refusals:** for 401, 403 and 429 from the embedding API the provider's own reason
  is logged (control characters removed, 300 characters at most). Other error bodies are never
  read because they can echo the input.

## Consequences

- Any OTLP backend works; none is required to run or test TRACE.
- A trace can stay open for as long as a job waits in the queue. Backends that expect short
  traces will show the job span arriving late.
- Database calls are not traced yet (no Npgsql instrumentation), so retrieval latency is
  visible only as the duration of the tool span.
- Repository names appear in the URLs of outgoing GitHub requests in spans. They are
  identifiers, not content, but they are visible to whoever can read the telemetry backend.
