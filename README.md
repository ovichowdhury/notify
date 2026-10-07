<p align="center">
  <img src="src/Notify.Web/wwwroot/images/logo.svg" alt="Notify" width="320" />
</p>

<p align="center">
  Self-hosted, multi-tenant email campaign manager.<br/>
  Bring your own SMTP server, upload a client list, write a template, send in parallel, watch the report.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-4f46e5.svg"></a>
  <img alt=".NET 9" src="https://img.shields.io/badge/.NET-9.0-512bd4.svg">
  <img alt="Tailwind CSS" src="https://img.shields.io/badge/Tailwind-3.4-06b6d4.svg">
</p>

---

## Contents

- [Features](#features)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Architecture](#architecture)
  - [Big picture](#big-picture)
  - [Tenancy model](#tenancy-model)
  - [Data model](#data-model)
  - [How a campaign is sent in parallel](#how-a-campaign-is-sent-in-parallel)
  - [Campaign and recipient state machines](#campaign-and-recipient-state-machines)
  - [Recipient import](#recipient-import)
  - [Template rendering](#template-rendering)
  - [Security notes](#security-notes)
  - [UI layer](#ui-layer)
- [Project layout](#project-layout)
- [Development](#development)
- [Deployment](#deployment)
- [Contributing](#contributing)
- [License](#license)

## Features

- **Accounts as tenants.** Sign up with email and password; every account has its own SMTP settings, campaigns,
  recipients and reports. Hand the same deployment to partner companies without them seeing each other's data.
- **Your own SMTP server.** Host, port, security mode, credentials and sender identity per account. Passwords are
  encrypted at rest. A "send test email" button validates the configuration.
- **Configurable parallelism.** Each account chooses how many SMTP connections send at once (1 to 50) and an
  optional pause between emails for rate-limited providers.
- **Campaigns with templates.** HTML subject and body with `{{Placeholder}}` substitution from any column of the
  uploaded file. Live preview with real recipient data.
- **CSV and Excel imports.** Header row required; the email column is detected by name or content; invalid and
  duplicate addresses are skipped and reported.
- **Background bulk sending** with a live report: delivered / failed / pending counts, per-recipient error
  messages, CSV export, retry of failed recipients, stop and resume.
- **Company profile** with logo shown across the UI, change password, seeded administrator account.

## Quick start

Prerequisites: .NET SDK 9.0 or later. Nothing else (SQLite is embedded, Tailwind CSS is precompiled).

```bash
git clone https://github.com/<your-org>/notify.git
cd notify/src/Notify.Web
dotnet run
```

Open **http://localhost:5600**, then either register a new tenant or sign in as the seeded administrator
(`admin@notify.local` / `Admin@12345`). Configure your SMTP server, create a campaign, upload
[`samples/recipients-sample.xlsx`](samples/recipients-sample.xlsx) and press **Run campaign**.

For a safe end-to-end test without real delivery, use an [Ethereal](https://ethereal.email) mailbox
(`smtp.ethereal.email`, port 587, StartTls), the Mailpit container described below, or the local sink in
`scripts/smtp-sink.py`.

### Quick start with Docker

```bash
git clone https://github.com/<your-org>/notify.git
cd notify
cp .env.example .env          # optional: change admin password and ports
docker compose up -d --build
```

| Service | URL | Purpose |
| --- | --- | --- |
| `notify` | http://localhost:5600 | The application |
| `mailpit` | http://localhost:8025 | Local SMTP catch-all with a web inbox for testing campaigns |

To send through Mailpit, set the SMTP settings in Notify to **Host `mailpit`, Port `1025`, Security `None`**,
no username or password. Every email then shows up in the Mailpit inbox instead of being delivered.

Data is persisted in named volumes: `notify-data` (SQLite database and the Data Protection keys that encrypt
SMTP passwords), `notify-uploads` (logos) and `mailpit-data`. `docker compose down` keeps them;
`docker compose down -v` deletes them.

## Configuration

All settings live in `src/Notify.Web/appsettings.json` and can be overridden with environment variables using
the `Section__Key` convention or with an `appsettings.Production.json`.

| Setting | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | `Data Source=App_Data/notify.db` | SQLite database file |
| `Kestrel:Endpoints:Http:Url` | `http://*:5600` | Listening address. `--urls` is ignored because this endpoint takes precedence; override with `--Kestrel:Endpoints:Http:Url=...` |
| `SeedAdmin:Email` / `Password` / `CompanyName` | `admin@notify.local` / `Admin@12345` / `Notify Administrator` | Administrator account created on first start and added to the `Admin` role |

Runtime state is stored under `src/Notify.Web/App_Data/` (database and Data Protection keys) and
`src/Notify.Web/wwwroot/uploads/` (logos). Both are git-ignored.

## Architecture

### Big picture

Notify is a single ASP.NET Core 9 MVC application. The web tier (controllers and Razor views) handles accounts,
settings and campaign management; a hosted background service does the actual sending so HTTP requests return
immediately and the UI can poll for progress.

```
 Browser ──HTTP──▶ Controllers ──▶ ApplicationDbContext (EF Core / SQLite)
                      │                       ▲
                      │ Run campaign          │ status + per-recipient results
                      ▼                       │
                CampaignQueue  ──▶  CampaignSenderService  ──▶  CampaignRunner
               (Channel<int>)       (BackgroundService)           │
                                                                  ├─ worker 1 ── SmtpClient #1 ──┐
                                                                  ├─ worker 2 ── SmtpClient #2 ──┤──▶ your SMTP server
                                                                  └─ worker N ── SmtpClient #N ──┘
```

| Layer | Technology |
| --- | --- |
| Web framework | ASP.NET Core 9 MVC, Razor views |
| Authentication | ASP.NET Core Identity (cookie auth, lockout, roles) |
| Persistence | Entity Framework Core 9 with SQLite (WAL mode), migrations applied on startup |
| Email | MailKit (`SmtpClient`, `MimeMessage`) |
| Imports | CsvHelper (CSV, delimiter auto-detection) and ClosedXML (XLSX) |
| Secrets at rest | ASP.NET Core Data Protection (keys persisted to `App_Data/keys`) |
| UI | Tailwind CSS 3.4 compiled with the standalone CLI, Inter font, jQuery unobtrusive validation |

### Tenancy model

The tenant **is** the user. `ApplicationUser` extends Identity's `IdentityUser` with `CompanyName` and
`LogoPath`. Every tenant-owned entity carries a `UserId` foreign key with cascade delete, and every controller
query filters on the signed-in user's id taken from the `NameIdentifier` claim. There is no shared data between
tenants, no tenant switcher and no cross-tenant admin UI yet; the seeded `Admin` role exists for future use.

The pattern every campaign action follows is `CampaignsController.FindOwnedAsync(id)`, which returns the
campaign only if it belongs to the current user and otherwise yields a 404.

### Data model

```
ApplicationUser (tenant)
 ├── SmtpSetting (1:1)          host, port, security, username, PasswordEncrypted, from name/email,
 │                              ConcurrencyLevel, DelayBetweenEmailsMs
 └── Campaign (1:N)             name, subject, BodyHtml, Status, ColumnsJson, timestamps, LastError
      └── CampaignRecipient (1:N)   email, name, DataJson (all source columns), Status, Error, SentAt, Attempts
```

`DataJson` keeps the whole source row so the template can reference any column. `ColumnsJson` on the campaign
lists the detected headers so the editor can offer placeholder chips and warn about placeholders that do not
match a column.

### How a campaign is sent in parallel

This is the core of the application, implemented in `Services/`.

**1. Queueing (HTTP request).** `CampaignsController.Run` validates that the campaign is not already active, that
SMTP settings exist and that there is at least one `Pending` recipient. It sets the campaign status to `Queued`
and writes the campaign id into `CampaignQueue`, an unbounded `System.Threading.Channels.Channel<int>`
registered as a singleton. The request then redirects to the details page.

**2. Dispatch (background).** `CampaignSenderService` is a `BackgroundService` that reads ids from the channel.
For each id it creates a linked `CancellationTokenSource`, registers it in `CampaignRunRegistry` (a
`ConcurrentDictionary<int, CancellationTokenSource>`) and starts `CampaignRunner.RunAsync` as a fire-and-forget
task. Campaigns from different tenants therefore run concurrently with each other. On application start the
service re-queues any campaign that was left `Running` or `Queued` by a previous process, so a restart during a
send resumes it instead of losing it.

**3. Sending (`CampaignRunner`).**

1. In a fresh DI scope it loads the campaign and the owner's `SmtpSetting`, decrypts the SMTP password, and
   reads the ids of all recipients whose status is `Pending`. If the campaign is no longer `Queued` (the user
   cancelled it while it waited) it exits without sending. Otherwise it marks the campaign `Running`.
2. It writes the recipient ids into a second in-memory channel and completes it.
3. It starts **N worker tasks**, where `N = clamp(ConcurrencyLevel, 1, 50)` capped at the number of recipients.
   Each worker:
   - creates its **own DI scope and `DbContext`**, and opens its **own MailKit `SmtpClient` connection**
     (MailKit clients are not thread-safe, so one connection per worker is the unit of parallelism);
   - reads recipient ids from the shared channel until it is empty. Because all workers pull from one channel,
     the load balances itself: a fast connection simply takes more recipients;
   - for each recipient renders the subject and body with that recipient's data, builds a `MimeMessage` with
     HTML and auto-generated plain-text parts, and calls `SendAsync`;
   - records `Sent` + `SentAt`, or `Failed` + the SMTP error message, increments `Attempts`, saves, and clears
     the change tracker so memory stays flat on large lists;
   - sleeps `DelayBetweenEmailsMs` if configured (per connection, so total throughput is roughly
     `N / delay`);
   - reconnects transparently if the server drops the connection, and gives up after three consecutive
     connection failures so a dead server does not mark every recipient as failed one by one.
   - A worker that cannot connect at all records the error and exits; the remaining workers keep draining the
     channel.
4. When all workers finish, the runner sets the final campaign status (see below) and `CompletedAt`.

**4. Progress.** Because each recipient row is saved as soon as it is processed, the details page simply polls
`GET /Campaigns/Status/{id}` every 2.5 seconds and reads aggregated counts from the database. SQLite runs in WAL
mode so these reads do not block the writers.

**Stopping.** The "Stop campaign" button calls `CampaignRunRegistry.Cancel(id)`, which cancels the token shared
by all workers. Workers stop between messages; recipients not yet processed stay `Pending`, so pressing **Run**
again resumes exactly where it stopped. If the campaign was still waiting in the queue, the controller marks it
`Cancelled` directly and the runner skips it when it is dequeued.

### Campaign and recipient state machines

Campaign:

```
Draft ──Run──▶ Queued ──picked up──▶ Running ──all workers done──▶ Completed
  ▲              │                      │                             
  │           Cancel                 Cancel ─────────────────────▶ Cancelled
  │              ▼                      │
  └── upload new file ◀─────────── Failed  (no SMTP settings, or recipients remain and every worker lost its connection)
```

Recipient: `Pending → Sent` or `Pending → Failed`; **Retry failed** flips `Failed → Pending` and re-queues the
campaign. Uploading a new file replaces all recipients and resets a finished campaign to `Draft`.

### Recipient import

`RecipientFileParser` reads `.csv`/`.txt` with CsvHelper (header row, trimmed values, delimiter detection) and
`.xlsx` with ClosedXML (first worksheet, formatted cell text). It then finds the email column by common header
names, by a header containing "mail", or by sampling values that parse as addresses, and the name column by
common header names. `RecipientImportService` validates each address, drops duplicates case-insensitively,
stores the full row as JSON, replaces the campaign's recipient list inside a transaction and returns counts
that are shown to the user.

### Template rendering

`TemplateRenderer` replaces `{{Column}}` tokens (case-insensitive, whitespace tolerant) with values from the
recipient's data. `Email` and `Name` are always available. Values inserted into the HTML body are HTML-encoded
so a hostile spreadsheet cannot inject markup; the subject is inserted verbatim. A plain-text alternative is
derived from the HTML for clients that prefer it.

### Security notes

- SMTP passwords are stored only as `PasswordEncrypted`, protected with Data Protection purpose
  `Notify.SmtpPassword.v1`. The key ring lives in `App_Data/keys`; keep it with the application.
- Identity is configured with lockout (10 failures, 10 minutes) and unique emails. Password rules are relaxed
  (minimum 6 characters) and can be tightened in `Program.cs`.
- Logo uploads are restricted to PNG/JPG/GIF/WEBP under 2 MB and stored with a random file name.
- All state-changing endpoints require the anti-forgery token.
- The app listens on plain HTTP; terminate TLS at a reverse proxy.

### UI layer

Razor views with Tailwind CSS. `Views/Shared/_Layout.cshtml` renders a sidebar application shell for signed-in
users and a marketing header for visitors, and resolves the current user to show the company name and logo.
Flash messages flow through `TempData["Success"]` / `TempData["Error"]` into `_Alerts.cshtml`. Reusable component
classes (`.btn`, `.card`, `.input`, `.badge`, `.th`/`.td`, validation hooks) are defined in
`Styles/app.css` and compiled into `wwwroot/css/tailwind.css`.

## Project layout

```
Notify.slnx
Dockerfile, docker-compose.yml, .env.example    Container build and local stack (app + Mailpit)
src/Notify.Web/
  Controllers/      Account, Dashboard, Settings (SMTP), Campaigns, Home
  Data/             ApplicationDbContext, DbSeeder, Migrations/
  Models/           Entities (ApplicationUser, SmtpSetting, Campaign, CampaignRecipient) and ViewModels/
  Services/         CampaignQueue + CampaignRunRegistry, CampaignSenderService, CampaignRunner,
                    SmtpClientFactory, SmtpPasswordProtector, RecipientFileParser,
                    RecipientImportService, TemplateRenderer
  Views/            Razor views and shared partials
  Styles/app.css    Tailwind source
  wwwroot/          Compiled CSS, logo and icons, vendored jQuery validation
scripts/            smoke-test.sh (end-to-end check), smtp-sink.py (local SMTP server),
                    get-tailwind.ps1 (downloads the git-ignored Tailwind CLI)
samples/            recipients-sample.xlsx

.claude/            Claude Code settings and project skills (run-app, add-migration, build-css, smoke-test, docker)
```

## Development

```bash
cd src/Notify.Web
dotnet build                                   # also recompiles Tailwind when scripts/tailwindcss.exe exists
dotnet run                                     # http://localhost:5600
dotnet ef migrations add <Name> -o Data/Migrations   # after changing entities; migrations apply on startup
```

**Styling.** Install the standalone Tailwind CLI once with `powershell -ExecutionPolicy Bypass -File scripts/get-tailwind.ps1`
(Windows) or download the binary for your OS into `scripts/`. The MSBuild target `BuildTailwindCss` then rebuilds
`wwwroot/css/tailwind.css` on every build. For live rebuilds while editing views, from the repository root:

```bash
scripts/tailwindcss.exe -c src/Notify.Web/tailwind.config.js -i src/Notify.Web/Styles/app.css -o src/Notify.Web/wwwroot/css/tailwind.css --watch
```

**Testing.** There is no unit-test project yet. `scripts/smoke-test.sh` is the end-to-end regression check and
runs in CI: with the app running it registers tenants, saves SMTP settings pointing at `scripts/smtp-sink.py`,
imports a CSV, sends a campaign with concurrency 4, asserts the report and the observed parallelism, and checks
tenant isolation.

```bash
cd src/Notify.Web && dotnet run          # terminal 1
bash scripts/smoke-test.sh               # terminal 2, repository root
```

## Deployment

**Docker (recommended).** The multi-stage `Dockerfile` publishes a Release build into the
`mcr.microsoft.com/dotnet/aspnet:9.0` image, runs as the non-root `app` user and exposes port 5600 with a health
check. `docker-compose.yml` wires it to named volumes; set `SEED_ADMIN_PASSWORD` in `.env` before the first start.
Mailpit is only a test helper and can be removed from the compose file for production, in which case also drop the
`depends_on` entry on `notify`.

```bash
docker compose up -d --build
docker compose logs -f notify
```

**Bare .NET.**

```bash
dotnet publish src/Notify.Web -c Release -o /opt/notify
cd /opt/notify && dotnet Notify.Web.dll
```

- Set `SeedAdmin__Password` (and ideally `SeedAdmin__Email`) before the first start.
- Persist `App_Data/` and `wwwroot/uploads/` across deployments; they hold the database, encryption keys and
  logos.
- Sending runs inside the web process. On IIS disable the application pool idle timeout, or run as a service
  (systemd, Windows Service) so long campaigns are not interrupted. An interrupted campaign resumes on restart.
- To move to SQL Server or PostgreSQL, swap the `UseSqlite` call in `Program.cs` for the matching provider and
  regenerate the migration; nothing else in the code is SQLite-specific.

## Contributing

Pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow and the
ground rules (tenant isolation, one SMTP connection per worker, migrations with schema changes), and
[SECURITY.md](SECURITY.md) for how to report vulnerabilities. This project follows the
[Contributor Covenant](CODE_OF_CONDUCT.md).

## License

[MIT](LICENSE)
