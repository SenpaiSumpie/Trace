# TaskBoard

TaskBoard is a small task-tracking service used as the TRACE demo and evaluation fixture.
It is synthetic: it exists to be indexed and asked questions about, not to be deployed.
Some defects are deliberate.

## API

All endpoints except `/health` require the `X-Api-Key` header.

| Method | Path | Description |
|---|---|---|
| `GET` | `/health` | Liveness probe. No authentication. |
| `GET` | `/tasks?page=1&pageSize=20` | Lists tasks, oldest first. Pages are 1-based. |
| `POST` | `/tasks` | Creates a task. |
| `POST` | `/reports` | Queues a summary report and returns its ID. |

## Layout

- `src/TaskBoard.Api` — ASP.NET Core minimal API.
- `web` — React client.
- `tests/TaskBoard.Tests` — unit tests.
- `docs/adr` — architecture decision records.

## Configuration

`Paging:MaxPageSize` caps the page size a client may request. See
[ADR 0001](docs/adr/0001-page-size-cap.md) for why the cap exists.
