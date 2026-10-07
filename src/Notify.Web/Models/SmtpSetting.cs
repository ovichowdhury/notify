using System.ComponentModel.DataAnnotations;

namespace Notify.Web.Models;

public enum SmtpSecurity
{
    Auto = 0,
    None = 1,
    StartTls = 2,
    SslOnConnect = 3
}

/// <summary>Per-user (tenant) SMTP configuration.</summary>
public class SmtpSetting
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    [Required, MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    [MaxLength(255)]
    public string? Username { get; set; }

    /// <summary>Password encrypted with ASP.NET Core Data Protection.</summary>
    public string? PasswordEncrypted { get; set; }

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    [Required, MaxLength(255)]
    public string FromEmail { get; set; } = string.Empty;

    [MaxLength(200)]
    public string FromName { get; set; } = string.Empty;

    /// <summary>Number of parallel SMTP connections used while sending a campaign.</summary>
    public int ConcurrencyLevel { get; set; } = 5;

    /// <summary>Optional pause after each email, per connection, to respect provider rate limits.</summary>
    public int DelayBetweenEmailsMs { get; set; } = 0;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
