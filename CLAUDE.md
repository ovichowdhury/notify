# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Notify is a multi-tenant email marketing campaign tool: ASP.NET Core 9 MVC (Razor views), EF Core 9 on SQLite,
ASP.NET Core Identity, MailKit for SMTP, CsvHelper/ClosedXML for recipient imports, Tailwind CSS for the UI.
Single project at `src/Notify.Web`; solution file is `Notify.slnx`. There is no unit-test project; the
regression check is `bash scripts/smoke-test.sh` against a running app (it starts `scripts/smtp-sink.py` itself).
CI (`.github/workflows/ci.yml`) builds with `-warnaserror` and runs that script.

Project skills in `.claude/skills/` cover the recurring workflows: `run-app`, `add-migration`, `build-css`,
`smoke-test`, `docker`. The README's Architecture section is the canonical description of the sending pipeline.

## Commands

All commands run from `src/Notify.Web` unless noted.

```bash
dotnet build
dotnet run                      # http://localhost:5600 (port fixed in appsettings.json Kestrel:Endpoints and launchSettings.json)
dotnet run --Kestrel:Endpoints:Http:Url=http://localhost:5601   # override port; --urls does NOT work (Kestrel config wins)
dotnet ef migrations add <Name> -o Data/Migrations               # dotnet-ef 9.x is installed globally
```

Docker (repo root): `docker compose up -d --build` starts the app on 5600 plus Mailpit (SMTP `mailpit:1025` inside
the network, inbox at http://localhost:8025). `docker compose down` keeps the named volumes (`notify-data` holds the
SQLite db and Data Protection keys); `down -v` wipes them. The image skips the Tailwind target, so commit a rebuilt
`tailwind.css` before building the image. To smoke-test the container:
`SINK_HOST=host.docker.internal bash scripts/smoke-test.sh`. The container and a local `dotnet run` both want
port 5600, so only one can be up at a time.

Migrations apply automatically at startup (`db.Database.Migrate()` in `Program.cs`), so there is no manual
`database update` step. `dotnet ef migrations add` builds *before* generating files; rebuild before
`dotnet run --no-build` or the new migration is missing from the DLL.

Tailwind: `Styles/app.css` compiles to `wwwroot/css/tailwind.css` (committed) via an MSBuild target
(`BuildTailwindCss`) that runs when `scripts/tailwindcss.exe` exists. Fetch the CLI once with
`powershell -ExecutionPolicy Bypass -File scripts/get-tailwind.ps1` (repo root). Watch mode (repo root):
`scripts\tailwindcss.exe -c src/Notify.Web/tailwind.config.js -i src/Notify.Web/Styles/app.css -o src/Notify.Web/wwwroot/css/tailwind.css --watch`.
Component classes (`.btn`, `.card`, `.input`, `.badge`, `.th/.td`, validation hooks) live only in `Styles/app.css`.

Runtime state lives in `App_Data/` (SQLite `notify.db`, Data Protection keys in `keys/`) and
`wwwroot/uploads/logos/`; both are git-ignored. Deleting `App_Data/keys` makes every stored SMTP password
unreadable (they are encrypted with Data Protection).

Build failures with "file is being used by another process" mean a `Notify.Web` instance is running
(often from the IDE); stop it before building.

## Architecture

**Tenancy is per user.** `ApplicationUser` (Identity) is the tenant. `SmtpSetting` (1:1), `Campaign` (1:N) and
`CampaignRecipient` (N per campaign) all hang off `UserId`. Every controller query filters by
`User.FindFirstValue(ClaimTypes.NameIdentifier)`; `CampaignsController.FindOwnedAsync` is the pattern for
loading a campaign and must be used for any new campaign action. There is an `Admin` role seeded by
`Data/DbSeeder.cs` from the `SeedAdmin` config section, but no admin-only pages exist yet.

**Sending pipeline** (all in `Services/`):

1. `CampaignsController.Run` sets status `Queued` and writes the campaign id to `CampaignQueue` (an in-process
   `Channel<int>`).
2. `CampaignSenderService` (BackgroundService) reads the channel and fires `CampaignRunner.RunAsync` per campaign,
   registering a `CancellationTokenSource` in `CampaignRunRegistry` so the UI "Stop" button can cancel. On startup it
   re-queues any campaign left `Running`/`Queued` by a previous process.
3. `CampaignRunner` loads pending recipient ids into a channel and spawns N workers, N = the user's
   `SmtpSetting.ConcurrencyLevel` (clamped 1..50). **Each worker owns one `SmtpClient` connection** (MailKit clients
   are not thread-safe) and its own DI scope/`DbContext`; it renders the template per recipient, sends, and persists
   `Sent`/`Failed` + error per row. Only `Pending` recipients are sent, so re-running resumes and "Retry failed" just
   flips `Failed` → `Pending` and re-enqueues. The runner skips campaigns whose status is no longer `Queued`
   (cancelled while waiting).
4. Final status: `Cancelled` if the token fired, `Failed` if recipients remain and workers reported connection
   errors, otherwise `Completed`.

Singletons: `CampaignQueue`, `CampaignRunRegistry`, `CampaignRunner`, `SmtpClientFactory`, `SmtpPasswordProtector`,
`RecipientFileParser`. Scoped: `RecipientImportService`. Anything singleton that touches the DB must create its own
scope via `IServiceScopeFactory`.

**Templates.** `TemplateRenderer` replaces `{{Column}}` (case-insensitive) with values from the recipient's
`DataJson` (all columns of the source row). Body values are HTML-encoded; the subject is not. `Email` and `Name`
are always injected as keys. `CampaignRunner.Deserialize` is the shared way to turn a recipient into the placeholder
dictionary (also used by `Preview`).

**Imports.** `RecipientFileParser` handles `.csv/.txt` (CsvHelper, delimiter auto-detect) and `.xlsx` (ClosedXML),
detects the email column by header name or by content, and the name column by header. `RecipientImportService`
replaces a campaign's recipients wholesale, skipping invalid and duplicate emails, and stores detected columns in
`Campaign.ColumnsJson` for the placeholder chips in the editor.

**SMTP passwords** are stored as `PasswordEncrypted` via `SmtpPasswordProtector` (Data Protection purpose
`Notify.SmtpPassword.v1`). On the settings form a blank password keeps the saved one; clearing the username clears
the password.

**UI shell.** `Views/Shared/_Layout.cshtml` renders a sidebar app shell when authenticated and a marketing header
otherwise; it resolves the current `ApplicationUser` to show company name/logo. Flash messages go through
`TempData["Success"]` / `TempData["Error"]` and `_Alerts.cshtml`. `Details.cshtml` polls
`Campaigns/Status/{id}` (JSON) every 2.5 s while a campaign is active and reloads when it finishes. Client-side
validation uses jQuery unobtrusive validation; its CSS hooks (`.field-validation-error`, etc.) are defined in
`Styles/app.css`. Bootstrap was removed; do not reintroduce it.

**Static files** use `app.UseStaticFiles()` (not `MapStaticAssets`) so runtime uploads under `wwwroot/uploads`
are served. HTTPS redirection is intentionally off; the app serves plain HTTP on 5600.

## Testing an SMTP flow locally

`samples/recipients-sample.xlsx` has 10 recipients with `Email, Name, Company, Plan, Discount, City` columns.
Ethereal (smtp.ethereal.email:587, StartTls) is a working catch-all for end-to-end sends; the "Send test email"
button on the SMTP settings page exercises the same `SmtpClientFactory` path as campaign sending.
