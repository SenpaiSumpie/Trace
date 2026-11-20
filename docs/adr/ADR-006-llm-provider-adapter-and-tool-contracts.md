# ADR-006: Provider-neutral chat model port and a fixed tool contract

Status: Accepted · 2026-11-20

## Context

Investigations need a language model that can call tools. The provider is not chosen yet
(handoff Section 18), tests must be deterministic, and provider SDK types must stay out of
Domain and Application (Section 3).

## Options

1. **Call a provider SDK from the orchestrator.** Fast to start; couples orchestration, tests
   and security review to one vendor's types and behaviour.
2. **A small port owned by the application** (`IChatModel`) with provider adapters in
   Infrastructure, and the tool loop owned by the application.
3. **A provider's or framework's agent loop** that executes tools itself. Less code, but the
   tool allowlist, argument binding and budgets would live outside our code.

## Decision

Option 2.

- `IChatModel.CompleteAsync` takes a system prompt, the conversation and tool definitions and
  returns either tool calls or final text. It never executes anything. Adapters translate to
  a provider API; they are given no credentials for, or access to, repositories.
- The **tool loop is ours** (`QuestionInvestigator`): a bounded state machine with at most 6
  model turns, 8 tool calls and 120,000 characters of tool output per investigation, and at
  most 8 search hits per call.
- The **tool contract is fixed and versioned**: exactly `search_code` and `read_file_range`,
  defined with the prompt in `QuestionPrompt` under one version string (`question-v1`) that is
  stored on every investigation together with the model ID.
- The model's final answer must be one JSON object (`sufficientEvidence`, `answer`,
  `citations`, `unknowns`). It is parsed and bounded; anything else fails the investigation
  with `invalid_model_output` rather than being shown.
- The stored result (`InvestigationResult`, schema version 1) is built by the application
  from validated parts. The API maps it to DTOs in `Trace.Contracts`; model text is never
  passed through as structured data.
- Providers are selected by `Chat:Provider` / `Embeddings:Provider`. There is no default, and
  the only implementations so far are deterministic fakes, which are refused in Production.

## Consequences

- Orchestration, limits and security properties are tested without any network or model.
- Each real provider needs an adapter (message and tool-call translation, retries, token
  usage). None exists yet, so TRACE cannot yet produce a model-written answer.
- Token usage and cost are not part of the port yet.
- No streaming: a turn is a single request/response.

## Revisit trigger

Choosing a provider (add its adapter and usage reporting); needing streaming output in the
UI; or evaluation results showing the JSON-answer protocol is a reliability problem, in
which case switch to the provider's structured-output feature behind the same port.
