using System.ComponentModel.DataAnnotations;

namespace Notify.Web.Models.ViewModels;

public class CampaignEditViewModel
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    [Display(Name = "Campaign name")]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    [Display(Name = "Email subject")]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Email template (HTML)")]
    public string BodyHtml { get; set; } = string.Empty;

    [Display(Name = "Recipients file (.csv or .xlsx)")]
    public IFormFile? RecipientsFile { get; set; }

    public IReadOnlyList<string> Columns { get; set; } = Array.Empty<string>();
}

public class CampaignListItemViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public CampaignStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Total { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public int Pending => Total - Sent - Failed;
}

public class CampaignCounts
{
    public int Total { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public int Processed => Sent + Failed;
    public int ProgressPercent => Total == 0 ? 0 : (int)Math.Round(100.0 * Processed / Total);
    public double DeliveryRate => Processed == 0 ? 0 : Math.Round(100.0 * Sent / Processed, 1);
}

public class CampaignDetailsViewModel
{
    public Campaign Campaign { get; set; } = null!;
    public CampaignCounts Counts { get; set; } = new();
    public IReadOnlyList<string> Columns { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingPlaceholders { get; set; } = Array.Empty<string>();
    public List<CampaignRecipient> Recipients { get; set; } = new();
    public RecipientStatus? StatusFilter { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages { get; set; } = 1;
    public int FilteredCount { get; set; }
    public bool SmtpConfigured { get; set; }
    public int ConcurrencyLevel { get; set; }
}

public class DashboardViewModel
{
    public string CompanyName { get; set; } = string.Empty;
    public bool SmtpConfigured { get; set; }
    public int CampaignCount { get; set; }
    public int ActiveCampaigns { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public int TotalFailed { get; set; }
    public int TotalPending { get; set; }
    public List<CampaignListItemViewModel> RecentCampaigns { get; set; } = new();
}
