using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notify.Web.Data;

namespace Notify.Test.Integration;

/// <summary>
/// Boots the real application (Program.cs, migrations, seeding, background sender) against a throwaway SQLite
/// file. Each factory instance gets its own database; test classes share one factory via IClassFixture.
/// </summary>
public sealed class NotifyWebFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin@12345";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"notify-web-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Visible to code that reads configuration after builder.Build() (e.g. DbSeeder).
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SeedAdmin:Email"] = AdminEmail,
                ["SeedAdmin:Password"] = AdminPassword,
                ["SeedAdmin:CompanyName"] = "Test Admin Co",
                ["Logging:LogLevel:Default"] = "Warning"
            });
        });

        // Program.cs captures the connection string before Build(), so configuration overrides arrive too late
        // for the DbContext. Replace the registered options instead; this runs before the container is built,
        // so the startup Migrate() already targets the temp file.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        });
    }

    /// <summary>A client that keeps cookies but does not follow redirects, so status codes can be asserted.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
