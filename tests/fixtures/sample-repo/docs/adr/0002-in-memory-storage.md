# ADR 0002: Keep tasks in memory

## Context

TaskBoard exists to be read, indexed and asked questions about. A database would add setup
steps for everyone who runs it and would teach nothing about the service itself.

## Decision

Tasks are held in a list inside `TaskRepository` and are never written to disk. The
repository is registered as a singleton so that every request sees the same list.

## Consequences

- All tasks are lost when the process restarts.
- Only one instance of the API can run: a second instance would have its own list.
- Task IDs start again at 1 after a restart, so clients must not store them.
