# ADR 0001: Cap the task list page size at 100

## Context

An early client requested `pageSize=50000` and the response took several seconds to
serialize, starving other requests. Clients do not need more than a screenful of tasks.

## Decision

`GET /tasks` rejects any `pageSize` above 100 with a 400 response instead of silently
clamping it. Silent clamping hid bugs in clients that assumed they had received everything.

The default page size is 20.

## Consequences

- Clients that need every task must page through the list.
- The cap is a constant in `PageRequest`; changing it is a breaking API change and needs a
  new ADR.
