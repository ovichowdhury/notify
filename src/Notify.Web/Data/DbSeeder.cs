using Microsoft.AspNetCore.Identity;
using Notify.Web.Models;

namespace Notify.Web.Data;

/// <summary>Creates the Admin role and the administrator account configured under "SeedAdmin".</summary>
public static class DbSeeder
{
    public const string AdminRole = "Admin";

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        if (!await roleManager.RoleExistsAsync(AdminRole))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(AdminRole));
            if (!roleResult.Succeeded)
            {
                logger.LogError("Could not create role {Role}: {Errors}", AdminRole, Describe(roleResult));
                return;
            }
        }

        var section = configuration.GetSection("SeedAdmin");
        var email = section["Email"]?.Trim();
        var password = section["Password"];
        var companyName = section["CompanyName"]?.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("SeedAdmin:Email or SeedAdmin:Password is not configured; no admin account was seeded.");
            return;
        }

        var admin = await userManager.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CompanyName = string.IsNullOrWhiteSpace(companyName) ? "Administrator" : companyName
            };

            var createResult = await userManager.CreateAsync(admin, password);
            if (!createResult.Succeeded)
            {
                logger.LogError("Could not create admin account {Email}: {Errors}", email, Describe(createResult));
                return;
            }
            logger.LogInformation("Seeded admin account {Email}.", email);
        }

        if (!await userManager.IsInRoleAsync(admin, AdminRole))
        {
            var roleResult = await userManager.AddToRoleAsync(admin, AdminRole);
            if (!roleResult.Succeeded)
                logger.LogError("Could not add {Email} to role {Role}: {Errors}", email, AdminRole, Describe(roleResult));
        }
    }

    private static string Describe(IdentityResult result)
        => string.Join("; ", result.Errors.Select(e => e.Description));
}
