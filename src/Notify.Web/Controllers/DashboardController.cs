using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Notify.Web.Data;
using Notify.Web.Models;
using Notify.Web.Models.ViewModels;

namespace Notify.Web.Controllers;

[Authorize]
public class DashboardController(ApplicationDbContext db, UserManager<ApplicationUser> userManager) : Controller
{
    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await userManager.GetUserAsync(User);

        var campaigns = db.Campaigns.Where(c => c.UserId == userId);
        var recipients = db.CampaignRecipients.Where(r => r.Campaign!.UserId == userId);

        var model = new DashboardViewModel
        {
            CompanyName = user?.CompanyName ?? string.Empty,
            SmtpConfigured = await db.SmtpSettings.AnyAsync(s => s.UserId == userId),
            CampaignCount = await campaigns.CountAsync(),
            ActiveCampaigns = await campaigns.CountAsync(c => c.Status == CampaignStatus.Running || c.Status == CampaignStatus.Queued),
            TotalRecipients = await recipients.CountAsync(),
            TotalSent = await recipients.CountAsync(r => r.Status == RecipientStatus.Sent),
            TotalFailed = await recipients.CountAsync(r => r.Status == RecipientStatus.Failed),
            TotalPending = await recipients.CountAsync(r => r.Status == RecipientStatus.Pending),
            RecentCampaigns = await campaigns
                .OrderByDescending(c => c.CreatedAt)
                .Take(5)
                .Select(c => new CampaignListItemViewModel
                {
                    Id = c.Id,
                    Name = c.Name,
                    Subject = c.Subject,
                    Status = c.Status,
                    CreatedAt = c.CreatedAt,
                    CompletedAt = c.CompletedAt,
                    Total = c.Recipients.Count(),
                    Sent = c.Recipients.Count(r => r.Status == RecipientStatus.Sent),
                    Failed = c.Recipients.Count(r => r.Status == RecipientStatus.Failed)
                })
                .ToListAsync()
        };

        return View(model);
    }
}
