using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notify.Web.Data;
using Notify.Web.Services;

namespace Notify.Test.TestSupport;

/// <summary>
/// A throwaway SQLite database file (WAL mode, like production) plus a service provider wired the same way as
/// Program.cs for the services under test. A file is used instead of ":memory:" because the campaign runner opens
/// several DbContexts concurrently, which a single in-memory connection cannot serve.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    public string FilePath { get; }
    public string ConnectionString => $"Data Source={FilePath}";

    public TestDatabase()
    {
        FilePath = Path.Combine(Path.GetTempPath(), $"notify-test-{Guid.NewGuid():N}.db");
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public DbContextOptions<ApplicationDbContext> Options =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(ConnectionString).Options;

    public ApplicationDbContext CreateContext() => new(Options);

    public ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(ConnectionString));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<SmtpPasswordProtector>();
        services.AddSingleton<SmtpClientFactory>();
        services.AddSingleton<RecipientFileParser>();
        services.AddScoped<RecipientImportService>();
        services.AddSingleton<CampaignQueue>();
        services.AddSingleton<CampaignRunRegistry>();
        services.AddSingleton<CampaignRunner>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(FilePath + suffix); } catch { /* best effort */ }
        }
    }
}
