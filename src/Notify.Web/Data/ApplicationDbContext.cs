using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Notify.Web.Models;

namespace Notify.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<SmtpSetting> SmtpSettings => Set<SmtpSetting>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<SmtpSetting>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne(x => x.User)
                .WithOne(u => u.SmtpSetting)
                .HasForeignKey<SmtpSetting>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Campaign>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User)
                .WithMany(u => u.Campaigns)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CampaignRecipient>(e =>
        {
            e.HasIndex(x => new { x.CampaignId, x.Status });
            e.HasOne(x => x.Campaign)
                .WithMany(c => c.Recipients)
                .HasForeignKey(x => x.CampaignId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
