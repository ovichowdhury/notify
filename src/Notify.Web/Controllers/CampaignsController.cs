using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Notify.Web.Constants;
using Notify.Web.Data;
using Notify.Web.Models;
using Notify.Web.Models.ViewModels;
using Notify.Web.Services;

namespace Notify.Web.Controllers;

[Authorize]
public class CampaignsController(
    ApplicationDbContext db,
    RecipientImportService importService,
    CampaignQueue queue,
    CampaignRunRegistry registry,
    ILogger<CampaignsController> logger) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ---------- List ----------

    public async Task<IActionResult> Index()
    {
        var campaigns = await db.Campaigns
            .Where(c => c.UserId == UserId)
            .OrderByDescending(c => c.CreatedAt)
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
            .ToListAsync();

        return View(campaigns);
    }

    // ---------- Create ----------

    [HttpGet]
    public IActionResult Create() => View(new CampaignEditViewModel { BodyHtml = CampaignDefaults.Template });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CampaignEditViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var campaign = new Campaign
        {
            UserId = UserId,
            Name = model.Name.Trim(),
            Subject = model.Subject.Trim(),
            BodyHtml = model.BodyHtml,
            Status = CampaignStatus.Draft
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();

        if (model.RecipientsFile is { Length: > 0 })
        {
            try
            {
                var result = await importService.ImportAsync(campaign, model.RecipientsFile);
                TempData["Success"] = DescribeImport(result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recipient import failed for campaign {CampaignId}", campaign.Id);
                TempData["Error"] = "Campaign created, but the recipients file could not be imported: " + ex.Message;
            }
        }
        else
        {
            TempData["Success"] = "Campaign created. Upload a recipients file to continue.";
        }

        return RedirectToAction(nameof(Details), new { id = campaign.Id });
    }

    // ---------- Edit ----------

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();
        if (campaign.IsActive)
        {
            TempData["Error"] = "A running campaign cannot be edited. Cancel it first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        return View(new CampaignEditViewModel
        {
            Id = campaign.Id,
            Name = campaign.Name,
            Subject = campaign.Subject,
            BodyHtml = campaign.BodyHtml,
            Columns = ReadColumns(campaign)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, CampaignEditViewModel model)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();
        if (campaign.IsActive)
        {
            TempData["Error"] = "A running campaign cannot be edited. Cancel it first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        model.Id = campaign.Id;
        model.Columns = ReadColumns(campaign);
        if (!ModelState.IsValid) return View(model);

        campaign.Name = model.Name.Trim();
        campaign.Subject = model.Subject.Trim();
        campaign.BodyHtml = model.BodyHtml;
        await db.SaveChangesAsync();

        if (model.RecipientsFile is { Length: > 0 })
        {
            try
            {
                var result = await importService.ImportAsync(campaign, model.RecipientsFile);
                TempData["Success"] = "Campaign saved. " + DescribeImport(result);
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Campaign saved, but the recipients file could not be imported: " + ex.Message;
            }
        }
        else
        {
            TempData["Success"] = "Campaign saved.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- Details / report ----------

    public async Task<IActionResult> Details(int id, RecipientStatus? status = null, int page = 1)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        var pageSize = CampaignDefaults.RecipientsPageSize;
        page = Math.Max(1, page);

        var recipientsQuery = db.CampaignRecipients.Where(r => r.CampaignId == id);
        var filtered = status.HasValue ? recipientsQuery.Where(r => r.Status == status.Value) : recipientsQuery;
        var filteredCount = await filtered.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(filteredCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var columns = ReadColumns(campaign);
        var placeholders = TemplateRenderer.ExtractPlaceholders(campaign.Subject + " " + campaign.BodyHtml);
        var known = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase) { "Email", "Name" };

        var smtp = await db.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == UserId);

        var model = new CampaignDetailsViewModel
        {
            Campaign = campaign,
            Counts = await CountAsync(id),
            Columns = columns,
            MissingPlaceholders = columns.Count == 0 ? Array.Empty<string>() : placeholders.Where(p => !known.Contains(p)).ToList(),
            Recipients = await filtered
                .OrderBy(r => r.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync(),
            StatusFilter = status,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            FilteredCount = filteredCount,
            SmtpConfigured = smtp is not null,
            ConcurrencyLevel = smtp?.ConcurrencyLevel ?? 0
        };

        return View(model);
    }

    /// <summary>JSON endpoint polled by the details page while a campaign is running.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(int id)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId);
        if (campaign is null) return NotFound();

        var counts = await CountAsync(id);
        return Json(new
        {
            status = campaign.Status.ToString(),
            isActive = campaign.IsActive,
            counts.Total,
            counts.Sent,
            counts.Failed,
            counts.Pending,
            counts.ProgressPercent,
            counts.DeliveryRate,
            lastError = campaign.LastError,
            startedAt = campaign.StartedAt,
            completedAt = campaign.CompletedAt
        });
    }

    /// <summary>Renders the template with the first recipient (or sample data) for an inline preview.</summary>
    [HttpGet]
    public async Task<IActionResult> Preview(int id)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        var first = await db.CampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == id)
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync();

        IReadOnlyDictionary<string, string> data = first is not null
            ? CampaignRunner.Deserialize(first)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = CampaignDefaults.SampleRecipientName,
                ["Email"] = CampaignDefaults.SampleRecipientEmail
            };

        var html = TemplateRenderer.Render(campaign.BodyHtml, data, htmlEncode: true);
        return Content(html, "text/html", Encoding.UTF8);
    }

    // ---------- Recipients upload ----------

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(int id, IFormFile? file)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose a .csv or .xlsx file to upload.";
            return RedirectToAction(nameof(Details), new { id });
        }

        try
        {
            var result = await importService.ImportAsync(campaign, file);
            TempData["Success"] = DescribeImport(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recipient import failed for campaign {CampaignId}", id);
            TempData["Error"] = "Import failed: " + ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- Run / cancel / retry ----------

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(int id)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        if (campaign.IsActive)
        {
            TempData["Error"] = "This campaign is already running.";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (!await db.SmtpSettings.AnyAsync(s => s.UserId == UserId))
        {
            TempData["Error"] = "Configure your SMTP settings before running a campaign.";
            return RedirectToAction("Smtp", "Settings");
        }

        var pending = await db.CampaignRecipients.CountAsync(r => r.CampaignId == id && r.Status == RecipientStatus.Pending);
        if (pending == 0)
        {
            TempData["Error"] = "There are no pending recipients to send to. Upload a recipients file or retry failed recipients.";
            return RedirectToAction(nameof(Details), new { id });
        }

        campaign.Status = CampaignStatus.Queued;
        campaign.LastError = null;
        await db.SaveChangesAsync();
        await queue.EnqueueAsync(campaign.Id);

        TempData["Success"] = $"Campaign queued. Sending to {pending} recipient(s).";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        if (!campaign.IsActive)
        {
            TempData["Error"] = "This campaign is not running.";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (!registry.Cancel(id))
        {
            // Queued but not started yet: mark it so the runner skips it, or simply reset status.
            campaign.Status = CampaignStatus.Cancelled;
            campaign.LastError = "Cancelled by user.";
            campaign.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        TempData["Success"] = "Cancellation requested. Emails already handed to the SMTP server will still be delivered.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryFailed(int id)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        if (campaign.IsActive)
        {
            TempData["Error"] = "Wait for the campaign to finish before retrying.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var reset = await db.CampaignRecipients
            .Where(r => r.CampaignId == id && r.Status == RecipientStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RecipientStatus.Pending)
                .SetProperty(r => r.Error, (string?)null));

        if (reset == 0)
        {
            TempData["Error"] = "There are no failed recipients to retry.";
            return RedirectToAction(nameof(Details), new { id });
        }

        campaign.Status = CampaignStatus.Queued;
        campaign.LastError = null;
        await db.SaveChangesAsync();
        await queue.EnqueueAsync(campaign.Id);

        TempData["Success"] = $"Retrying {reset} failed recipient(s).";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        if (campaign.IsActive)
        {
            TempData["Error"] = "Cancel the campaign before deleting it.";
            return RedirectToAction(nameof(Details), new { id });
        }

        db.Campaigns.Remove(campaign);
        await db.SaveChangesAsync();
        TempData["Success"] = $"Campaign \"{campaign.Name}\" deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ---------- Export ----------

    [HttpGet]
    public async Task<IActionResult> Export(int id, RecipientStatus? status = null)
    {
        var campaign = await FindOwnedAsync(id);
        if (campaign is null) return NotFound();

        var query = db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id);
        if (status.HasValue) query = query.Where(r => r.Status == status.Value);
        var rows = await query.OrderBy(r => r.Id).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Email,Name,Status,SentAt,Attempts,Error");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.Email), Csv(r.Name), r.Status.ToString(),
                r.SentAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                r.Attempts.ToString(), Csv(r.Error)));
        }

        var suffix = status.HasValue ? $"-{status.Value.ToString().ToLowerInvariant()}" : string.Empty;
        var fileName = $"campaign-{id}{suffix}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", fileName);
    }

    // ---------- Helpers ----------

    private Task<Campaign?> FindOwnedAsync(int id)
        => db.Campaigns.FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId);

    private async Task<CampaignCounts> CountAsync(int campaignId)
    {
        var grouped = await db.CampaignRecipients
            .Where(r => r.CampaignId == campaignId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var counts = new CampaignCounts();
        foreach (var g in grouped)
        {
            switch (g.Status)
            {
                case RecipientStatus.Sent: counts.Sent = g.Count; break;
                case RecipientStatus.Failed: counts.Failed = g.Count; break;
                default: counts.Pending = g.Count; break;
            }
        }
        counts.Total = counts.Sent + counts.Failed + counts.Pending;
        return counts;
    }

    private static IReadOnlyList<string> ReadColumns(Campaign campaign)
    {
        if (string.IsNullOrEmpty(campaign.ColumnsJson)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(campaign.ColumnsJson) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string DescribeImport(ImportResult result)
    {
        var message = $"Imported {result.Imported} recipient(s).";
        if (result.SkippedInvalid > 0) message += $" Skipped {result.SkippedInvalid} row(s) with an invalid email.";
        if (result.SkippedDuplicates > 0) message += $" Skipped {result.SkippedDuplicates} duplicate(s).";
        return message;
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
