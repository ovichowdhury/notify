using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Notify.Test.TestSupport;
using Notify.Web.Data;
using Notify.Web.Models;

namespace Notify.Test.Services;

public class DbSeederTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ServiceProvider _services;

    public DbSeederTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_db.ConnectionString));
        services.AddIdentityCore<ApplicationUser>(o => o.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private static IConfiguration Config(string? email, string? password, string? company = "Seed Co")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SeedAdmin:Email"] = email,
            ["SeedAdmin:Password"] = password,
            ["SeedAdmin:CompanyName"] = company
        }).Build();

    private async Task SeedAsync(IConfiguration config)
    {
        using var scope = _services.CreateScope();
        await DbSeeder.SeedAsync(scope.ServiceProvider, config, NullLogger.Instance);
    }

    [Fact]
    public async Task CreatesRoleAndAdminUserInRole()
    {
        await SeedAsync(Config("admin@seed.local", "Admin@12345"));

        using var scope = _services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        Assert.True(await roles.RoleExistsAsync(DbSeeder.AdminRole));
        var admin = await users.FindByEmailAsync("admin@seed.local");
        Assert.NotNull(admin);
        Assert.Equal("Seed Co", admin.CompanyName);
        Assert.True(admin.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(admin, "Admin@12345"));
        Assert.True(await users.IsInRoleAsync(admin, DbSeeder.AdminRole));
    }

    [Fact]
    public async Task IsIdempotent_SecondRunChangesNothing()
    {
        var config = Config("admin@seed.local", "Admin@12345");
        await SeedAsync(config);
        await SeedAsync(Config("admin@seed.local", "DifferentPassword@1", "Other Co"));

        using var scope = _services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(1, await users.Users.CountAsync());
        var admin = (await users.FindByEmailAsync("admin@seed.local"))!;
        Assert.Equal("Seed Co", admin.CompanyName);
        Assert.True(await users.CheckPasswordAsync(admin, "Admin@12345"));
    }

    [Theory]
    [InlineData(null, "Admin@12345")]
    [InlineData("", "Admin@12345")]
    [InlineData("admin@seed.local", null)]
    [InlineData("admin@seed.local", "  ")]
    public async Task MissingEmailOrPassword_CreatesRoleButNoUser(string? email, string? password)
    {
        await SeedAsync(Config(email, password));

        using var scope = _services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().RoleExistsAsync(DbSeeder.AdminRole));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users.CountAsync());
    }

    [Fact]
    public async Task PasswordRejectedByPolicy_DoesNotCreateUserOrThrow()
    {
        await SeedAsync(Config("admin@seed.local", "weak"));

        using var scope = _services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("admin@seed.local"));
    }

    [Fact]
    public async Task ExistingUserIsPromotedToAdminWithoutChangingPassword()
    {
        using (var scope = _services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var existing = new ApplicationUser { UserName = "boss@seed.local", Email = "boss@seed.local", CompanyName = "Boss Co" };
            Assert.True((await users.CreateAsync(existing, "Original@123")).Succeeded);
        }

        await SeedAsync(Config("boss@seed.local", "Ignored@123", "Ignored Co"));

        using var verify = _services.CreateScope();
        var manager = verify.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByEmailAsync("boss@seed.local"))!;
        Assert.True(await manager.IsInRoleAsync(user, DbSeeder.AdminRole));
        Assert.Equal("Boss Co", user.CompanyName);
        Assert.True(await manager.CheckPasswordAsync(user, "Original@123"));
    }

    [Fact]
    public async Task BlankCompanyNameFallsBackToAdministrator()
    {
        await SeedAsync(Config("admin@seed.local", "Admin@12345", company: "  "));

        using var scope = _services.CreateScope();
        var admin = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("admin@seed.local");
        Assert.Equal("Administrator", admin!.CompanyName);
    }
}
