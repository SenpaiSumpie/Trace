# Operations

Notes for whoever runs a TaskBoard instance.

## Health

`GET /health` returns `{"status":"ok"}` while the process is up. It does not check the
report worker, so a stuck worker does not make the probe fail.

## Rotating the API key

1. Choose a new key and set it in the `Auth__ApiKey` environment variable.
2. Restart the API. The key is read once at startup, so a running process keeps the old one.
3. Update `VITE_TASKBOARD_API_KEY` for the web client and rebuild it.

There is no period in which both keys work: clients fail with 401 between steps 2 and 3.

## Reports

Reports are generated one at a time, in the order they were queued. Queued reports are held
in memory and are lost on restart; queue them again afterwards.

## Logs

The API writes logs to standard output only. Set `Logging__LogLevel__Default` to `Debug` for
more detail.
