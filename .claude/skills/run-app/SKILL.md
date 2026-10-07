---
name: run-app
description: Start, verify and stop the Notify web app locally (port 5600). Use when asked to run the app, check that a change works in the browser, or when a build fails because the app is already running.
---

# Run the Notify app

## Start

```bash
cd src/Notify.Web
dotnet run
```

- Listens on **http://localhost:5600** (fixed in `appsettings.json` → `Kestrel:Endpoints:Http:Url`).
- To use another port: `dotnet run --Kestrel:Endpoints:Http:Url=http://localhost:5601`.
  `--urls` is ignored because the configured Kestrel endpoint takes precedence.
- The SQLite database, migrations and the seeded admin (`admin@notify.local` / `Admin@12345`, see `SeedAdmin` in
  `appsettings.json`) are created automatically on first start.
- When running in the background, redirect output to a log file and wait with
  `curl --retry 30 --retry-delay 2 --retry-all-errors --retry-connrefused http://localhost:5600/Account/Login`.

## Verify

- Anonymous: `/` (landing), `/Account/Login`, `/Account/Register` must return 200.
- Authenticated pages need the Identity cookie: fetch `/Account/Login`, extract the `__RequestVerificationToken`
  hidden input, POST it with `Email`/`Password`, keep the cookie jar, then GET `/Dashboard`, `/Campaigns`,
  `/Settings/Smtp`, `/Account/Profile`. `scripts/smoke-test.sh` does all of this end to end.

## Stop

A running instance locks `bin/Debug/net9.0/Notify.Web.exe`, so `dotnet build` fails with MSB3021/MSB3027
("being used by another process"). Stop it before building:

```powershell
Get-Process -Name "Notify.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
```

The instance may have been started from the user's IDE; say so when you stop it.
