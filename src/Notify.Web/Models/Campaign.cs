using System.ComponentModel.DataAnnotations;

namespace Notify.Web.Models;

public enum CampaignStatus
{
    Draft = 0,
    Queued = 1,
    Running = 2,
    Completed = 3,
    Cancelled = 4,
    Failed = 5
}

public class Campaign
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string BodyHtml { get; set; } = string.Empty;

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    [MaxLength(255)]
    public string? RecipientFileName { get; set; }

    /// <summary>JSON array of the column names detected in the uploaded file (available as placeholders).</summary>
    public string? ColumnsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public ICollection<CampaignRecipient> Recipients { get; set; } = new List<CampaignRecipient>();

    public bool IsActive => Status is CampaignStatus.Queued or CampaignStatus.Running;
}
