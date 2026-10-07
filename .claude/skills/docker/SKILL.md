---
name: docker
description: Build, run, test and inspect Notify with Docker Compose (app + Mailpit, persistent volumes). Use when asked to containerize, run in Docker, check the compose stack, or verify data persistence.
---

# Docker Compose workflow

Files: `Dockerfile` (multi-stage, runs as non-root `app`, health check on `/Account/Login`), `docker-compose.yml`
(services `notify` and `mailpit`, named volumes), `.env.example` (ports and admin seed), `.dockerignore`.

## Commands (repository root)

```bash
docker compose build                 # build image notify:local
docker compose up -d                 # start; app http://localhost:5600, Mailpit UI http://localhost:8025
docker compose ps                    # both services should be "healthy"
docker compose logs -f notify
docker compose down                  # stop, keep volumes (database, keys, logos)
docker compose down -v               # stop AND delete all data
```

Wait for health rather than sleeping:
`docker inspect --format '{{.State.Health.Status}}' notify` returns `healthy`.

## Testing inside the container

- Smoke test: `SINK_HOST=host.docker.internal bash scripts/smoke-test.sh` (the sink binds 0.0.0.0 so the
  container can reach it; plain `127.0.0.1` would point at the container itself).
- Mailpit round trip: save SMTP settings with Host `mailpit`, Port `1025`, Security `None` (enum value 1), send a
  test email, then `curl -s http://localhost:8025/api/v1/messages` and check `total`.
- Persistence: `docker compose down && docker compose up -d`, then confirm the admin can still log in, the SMTP
  host is still saved, and the fresh log does **not** contain `Seeded admin account`.

## Gotchas

- Git Bash rewrites `/app/...` arguments into Windows paths. Prefix docker commands that pass container paths with
  `MSYS_NO_PATHCONV=1`, or wrap them in `sh -c '...'`.
- The image does not contain the Tailwind CLI; it ships the committed `wwwroot/css/tailwind.css`. Rebuild and
  commit the CSS before building the image after view changes.
- Port 5600 is shared with a local `dotnet run`; stop one before starting the other.
- `--urls` is ignored; the port is set through `Kestrel__Endpoints__Http__Url` (already set in the Dockerfile).
- Removing Mailpit for production also requires removing the `depends_on` block on `notify`.
