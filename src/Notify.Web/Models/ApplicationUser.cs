using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Notify.Web.Models;

/// <summary>
/// A tenant. Every SMTP setting, campaign and recipient belongs to exactly one user.
/// </summary>
public class ApplicationUser : IdentityUser
{
    [MaxLength(200)]
    public string CompanyName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? LogoPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public SmtpSetting? SmtpSetting { get; set; }

    public ICollection<Campaign> Campaigns { get; set; } = new List<Campaign>();
}
