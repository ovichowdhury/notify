using System.ComponentModel.DataAnnotations;

namespace Notify.Web.Models;

public enum RecipientStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2
}

public class CampaignRecipient
{
    public int Id { get; set; }

    public int CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    [Required, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Name { get; set; }

    /// <summary>All columns of the source row as a JSON object, used for template placeholders.</summary>
    public string? DataJson { get; set; }

    public RecipientStatus Status { get; set; } = RecipientStatus.Pending;

    [MaxLength(1000)]
    public string? Error { get; set; }

    public DateTime? SentAt { get; set; }

    public int Attempts { get; set; }
}
