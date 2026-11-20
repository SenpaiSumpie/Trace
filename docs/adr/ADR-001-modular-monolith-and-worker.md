# ADR-001: Modular monolith API plus one background worker

Status: Accepted · 2026-11-20

## Context

TRACE v0.1 needs an HTTP API, long-running indexing and investigation jobs, and a web UI.
The handoff (Sections 3 and 19) rules out microservices, Kubernetes and extra infrastructure
without evidence they are needed. Long-running work must not run inside request handling.

## Options

1. **Single process** — API hosts background jobs. Simplest, but a deploy or crash of the API
   interrupts jobs, and job load competes with request latency.
2. **Modular monolith + one worker process** — one codebase and one database, two composition
   roots (`Trace.Api`, `Trace.Worker`) over shared `Application`/`Infrastructure` modules.
3. **Separate services** — independent deployables with a message broker. Operational cost with
   no current scale or ownership reason.

## Decision

Option 2. Projects and their permitted references:

| Project | May reference |
|---|---|
| `Trace.Domain` | nothing |
| `Trace.Contracts` | nothing |
| `Trace.Application` | Domain, Contracts |
| `Trace.Infrastructure` | Application, Domain, Contracts |
| `Trace.Api`, `Trace.Worker` | Application, Infrastructure, Contracts, Domain |

The rule is enforced by `tests/Trace.UnitTests/ProjectDependencyRuleTests.cs`, which reads the
project files. Provider SDK types stay out of Domain and core Application interfaces.

## Consequences

- One build, one schema, one deployment unit per process; module boundaries are compile-time.
- API and worker communicate only through the database (durable jobs, added in a later P0 task).
- The root namespace `Trace` shadows `System.Diagnostics.Trace` inside this codebase; use
  `ILogger`/OpenTelemetry rather than that class.

## Revisit trigger

Measured job throughput, independent scaling needs, or team ownership boundaries that the
single database and deployment cadence cannot serve.
