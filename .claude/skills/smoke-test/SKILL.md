---
name: smoke-test
description: Run the end-to-end smoke test (register, SMTP settings, CSV import, parallel send through a local SMTP sink, report, tenant isolation). Use after changing controllers, services, views or the sending pipeline.
---

# End-to-end smoke test

There is no unit-test project; `scripts/smoke-test.sh` is the regression check. It drives the real HTTP
endpoints with curl and sends real SMTP traffic to `scripts/smtp-sink.py`, a tiny in-process SMTP server that
accepts everything and reports the peak number of concurrent connections.

## Run

1. Start the app (see the `run-app` skill), e.g. `cd src/Notify.Web && dotnet run`.
2. From the repository root:

```bash
bash scripts/smoke-test.sh                       # defaults: app http://localhost:5600, sink port 2525
bash scripts/smoke-test.sh http://localhost:5601 2526
```

Requirements: bash, curl, python3 (or `python`). No extra packages.

## What it asserts

- Registration redirects to the SMTP settings page and all authenticated pages return 200.
- SMTP settings save and the "send test email" path succeeds against the sink.
- A CSV with 20 valid rows, 1 invalid email and 1 duplicate imports as 20 recipients.
- `Run` completes with `status=Completed`, `sent=20`, `failed=0`.
- The sink observed exactly the configured concurrency (4 parallel connections).
- A second tenant gets 404 on the first tenant's campaign and status endpoints.

The script creates throwaway users named `smoke<random>@example.com` in the local SQLite database; delete
`src/Notify.Web/App_Data/notify.db` to reset, or leave them (they do not affect other tenants).

Exit code is non-zero on any failed assertion; the output names the failing step.
