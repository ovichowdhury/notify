---
name: run-tests
description: Run or extend the xUnit test suite in src/Notify.Test (unit, service and integration tests with coverage). Use after any code change, when asked to add tests, or to diagnose a failing test.
---

# Test suite

`src/Notify.Test` (xUnit, net9.0) covers the whole app without external services:

| Folder | Scope | Support used |
| --- | --- | --- |
| `Unit/` | `TemplateRenderer`, `RecipientFileParser`, `SmtpPasswordProtector`, `SmtpClientFactory`, queue/registry, models | `FakeSmtpServer` for the client factory |
| `Services/` | `RecipientImportService`, `CampaignRunner`, `CampaignSenderService`, `DbSeeder` | `TestDatabase` (temp SQLite file + DI container mirroring Program.cs), `FakeSmtpServer` |
| `Integration/` | Every controller action over HTTP, tenant isolation | `NotifyWebFactory` (`WebApplicationFactory<Program>`), `ClientExtensions` (anti-forgery aware form posts) |

## Commands (repository root)

```bash
dotnet test                                                        # everything, ~10 s
dotnet test --filter "FullyQualifiedName~Integration"              # a layer
dotnet test --filter "FullyQualifiedName~CampaignRunnerTests"      # a class
dotnet test --filter "Name~Run_CancellationStopsBetweenMessages"   # a method
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults
```

Show failure details: `dotnet test --logger "console;verbosity=normal"` and read the `Error Message` block.
`TestResults/` is git-ignored; summarise `coverage.cobertura.xml` with the `line-rate` attribute on the root.

## Writing tests here

- Service tests: `using var db = new TestDatabase(); var sp = db.BuildServices();` then seed with `TestData.*`
  helpers and resolve services from `sp`. Open a fresh `db.CreateContext()` to assert persisted state.
- Sending tests: `await using var server = new FakeSmtpServer { DataDelay = ..., RejectRecipient = ..., RequiredCredentials = ... };`
  and point `TestData.Smtp(userId, server.Port)` at it. `server.Messages` holds what was sent,
  `server.PeakConcurrentConnections` proves parallelism, `FakeSmtpServer.ClosedPort()` simulates an unreachable host.
- Integration tests: take `NotifyWebFactory` via `IClassFixture`, call `factory.CreateBrowser()` (cookies on,
  redirects off), then `RegisterAsync`, `SaveSmtpAsync`, `CreateCampaignAsync`, `PostFormAsync` /
  `PostWithTokenAsync` (anti-forgery token is extracted from the form page), `FollowAsync` to read flash messages,
  `WaitForCompletionAsync` to poll `Campaigns/Status/{id}`.
- Every new controller action needs a happy-path test and a case in `TenantIsolationTests`.
- Assert on rendered text the user sees (flash messages, labels); for HTML-encoded values remember `&quot;`.
- Do not assert on MIME header quoting details (MimeKit quotes display names only when needed).

## Gotchas

- Configuration overrides from `ConfigureAppConfiguration` are not visible to code in `Program.cs` that runs before
  `builder.Build()`; that is why `NotifyWebFactory` replaces `DbContextOptions<ApplicationDbContext>` in
  `ConfigureTestServices` instead of overriding the connection string.
- Integration tests write Data Protection keys to `src/Notify.Web/App_Data/keys` and logo uploads to
  `src/Notify.Web/wwwroot/uploads/logos` (both git-ignored). The logo test removes its file.
- A `Notify.Web` instance running from the IDE locks the build output; stop it before `dotnet test`.
