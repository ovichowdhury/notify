using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Notify.Web.Data;
using Notify.Web.Models;

namespace Notify.Web.Services;

public record ImportResult(int Imported, int SkippedInvalid, int SkippedDuplicates, IReadOnlyList<string> Columns);

/// <summary>Replaces a campaign's recipient list with the rows of an uploaded CSV / XLSX file.</summary>
public class RecipientImportService(ApplicationDbContext db, RecipientFileParser parser)
{
    public async Task<ImportResult> ImportAsync(Campaign campaign, IFormFile file, CancellationToken ct = default)
    {
        if (campaign.IsActive)
            throw new InvalidOperationException("Recipients cannot be changed while the campaign is running.");

        ParsedRecipientFile parsed;
        await using (var stream = file.OpenReadStream())
        {
            parsed = parser.Parse(stream, file.FileName);
        }

        var emailColumn = parsed.EmailColumn!;
        var nameColumn = parsed.NameColumn;

        var recipients = new List<CampaignRecipient>(parsed.Rows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = 0;
        var duplicates = 0;

        foreach (var row in parsed.Rows)
        {
            row.TryGetValue(emailColumn, out var email);
            email = email?.Trim();
            if (!RecipientFileParser.IsValidEmail(email))
            {
                invalid++;
                continue;
            }
            if (!seen.Add(email!))
            {
                duplicates++;
                continue;
            }

            string? name = null;
            if (nameColumn is not null && row.TryGetValue(nameColumn, out var n) && !string.IsNullOrWhiteSpace(n))
                name = n.Trim();

            // Always expose Email and Name as placeholders regardless of the original header names.
            var data = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase);
            data.TryAdd("Email", email!);
            if (name is not null) data.TryAdd("Name", name);

            recipients.Add(new CampaignRecipient
            {
                CampaignId = campaign.Id,
                Email = email!,
                Name = name,
                DataJson = JsonSerializer.Serialize(data),
                Status = RecipientStatus.Pending
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.CampaignRecipients.Where(r => r.CampaignId == campaign.Id).ExecuteDeleteAsync(ct);
        db.CampaignRecipients.AddRange(recipients);

        campaign.RecipientFileName = Path.GetFileName(file.FileName);
        campaign.ColumnsJson = JsonSerializer.Serialize(parsed.Columns);
        campaign.LastError = null;
        if (campaign.Status is CampaignStatus.Completed or CampaignStatus.Cancelled or CampaignStatus.Failed)
        {
            campaign.Status = CampaignStatus.Draft;
            campaign.StartedAt = null;
            campaign.CompletedAt = null;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ImportResult(recipients.Count, invalid, duplicates, parsed.Columns);
    }
}
