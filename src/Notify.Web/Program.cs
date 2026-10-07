using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Notify.Web.Data;
using Notify.Web.Models;
using Notify.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------- Database ----------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=App_Data/notify.db";
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connectionString));

// ---------- Identity (each user is a tenant) ----------
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 6;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});

// Keys used to encrypt SMTP passwords and auth cookies live with the app so they survive redeploys.
builder.Services.AddDataProtection()
    .SetApplicationName("Notify")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));

// ---------- Application services ----------
builder.Services.AddSingleton<SmtpPasswordProtector>();
builder.Services.AddSingleton<SmtpClientFactory>();
builder.Services.AddSingleton<RecipientFileParser>();
builder.Services.AddScoped<RecipientImportService>();
builder.Services.AddSingleton<CampaignQueue>();
builder.Services.AddSingleton<CampaignRunRegistry>();
builder.Services.AddSingleton<CampaignRunner>();
builder.Services.AddHostedService<CampaignSenderService>();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50 MB recipient files
});

builder.Services.AddControllersWithViews();

var app = builder.Build();

// ---------- Create / migrate the database on startup ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "App_Data"));
    db.Database.Migrate();
    // WAL mode lets the parallel senders write while the UI reads.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");
    await DbSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, seedLogger);
}

Directory.CreateDirectory(Path.Combine(app.Environment.WebRootPath, "uploads", "logos"));

// ---------- HTTP pipeline ----------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// The app listens on plain HTTP (port 5600, see appsettings.json "Kestrel"); terminate TLS at a reverse proxy if needed.
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
