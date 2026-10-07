# Contributing to Notify

Thanks for your interest in improving Notify. This document explains how to set up a development
environment, how the project is organised, and what we expect from a pull request.

## Prerequisites

- .NET SDK 9.0 or later (`dotnet --version`)
- `dotnet-ef` tool (`dotnet tool install --global dotnet-ef`) if you change the data model
- Python 3 for the smoke test's local SMTP sink (no packages required)
- Optional: Docker with Compose v2 to run the app and a Mailpit test inbox in containers (`docker compose up -d --build`)
- Optional: the standalone Tailwind CLI for UI work (`powershell -ExecutionPolicy Bypass -File scripts/get-tailwind.ps1`
  on Windows, or download the matching binary from the Tailwind releases page into `scripts/`)

## Getting started

```bash
git clone <your fork>
cd Notify/src/Notify.Web
dotnet run
```

Open http://localhost:5600 and sign in with the seeded admin (`admin@notify.local` / `Admin@12345`) or register
a new tenant. The SQLite database and migrations are created automatically under `App_Data/`.

## Running the checks

There is no unit-test project yet; the end-to-end smoke test is the regression suite and CI runs it:

```bash
# terminal 1
cd src/Notify.Web && dotnet run
# terminal 2, repository root
bash scripts/smoke-test.sh
```

It registers throwaway tenants, configures SMTP against a local sink, imports a CSV, runs a campaign with
concurrency 4 and verifies the report, the observed parallelism and tenant isolation. Please run it before
opening a pull request, and extend it when you add behaviour.

## Where things live

Read the **Architecture** section of the README first. In short:

| Area | Location |
| --- | --- |
| Entities and view models | `src/Notify.Web/Models` |
| EF Core context, seeding, migrations | `src/Notify.Web/Data` |
| Sending pipeline, import, templating, SMTP | `src/Notify.Web/Services` |
| MVC controllers and Razor views | `src/Notify.Web/Controllers`, `src/Notify.Web/Views` |
| Tailwind source | `src/Notify.Web/Styles/app.css` (compiled to `wwwroot/css/tailwind.css`) |
| Local test tooling | `scripts/` |

## Ground rules

- **Tenancy first.** Every query that touches `SmtpSetting`, `Campaign` or `CampaignRecipient` must filter by the
  current user's id. Use `FindOwnedAsync` in `CampaignsController` as the model. A pull request that lets one tenant
  read another tenant's data will not be merged.
- **One SMTP connection per worker.** MailKit's `SmtpClient` is not thread-safe. Do not share a client across tasks.
- **Singletons do not hold a `DbContext`.** Create a scope with `IServiceScopeFactory` inside background code.
- **Schema changes ship with a migration** (`dotnet ef migrations add <Name> -o Data/Migrations`) and the updated
  model snapshot.
- **Styling goes through Tailwind.** Reusable classes belong in `Styles/app.css`; rebuild `tailwind.css` and commit it.
- **Secrets stay out of the repo.** Use environment variables or `appsettings.Production.json` (git-ignored) for
  real SMTP credentials and the admin seed password.

## Pull requests

1. Fork and create a branch from `main` (`feature/short-description` or `fix/short-description`).
2. Keep the change focused; unrelated refactors belong in their own PR.
3. Make sure `dotnet build` is warning-free and `scripts/smoke-test.sh` passes.
4. Fill in the pull request template: what changed, why, and how you tested it. Screenshots for UI changes.
5. One approving review is required. Maintainers may squash on merge.

## Reporting bugs and requesting features

Use the issue templates. For security problems, do **not** open a public issue; see `SECURITY.md`.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating you agree to uphold it.
