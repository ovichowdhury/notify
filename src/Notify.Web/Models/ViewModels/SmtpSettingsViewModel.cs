using System.ComponentModel.DataAnnotations;

namespace Notify.Web.Models.ViewModels;

public class SmtpSettingsViewModel
{
    [Required, MaxLength(255)]
    [Display(Name = "SMTP host")]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    [Display(Name = "Security")]
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    [MaxLength(255)]
    [Display(Name = "Username")]
    public string? Username { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string? Password { get; set; }

    /// <summary>True when a password is already stored; leaving the field blank keeps it.</summary>
    public bool HasSavedPassword { get; set; }

    [Required, EmailAddress, MaxLength(255)]
    [Display(Name = "From email")]
    public string FromEmail { get; set; } = string.Empty;

    [MaxLength(200)]
    [Display(Name = "From name")]
    public string FromName { get; set; } = string.Empty;

    [Range(1, 50)]
    [Display(Name = "Concurrency level (parallel connections)")]
    public int ConcurrencyLevel { get; set; } = 5;

    [Range(0, 60000)]
    [Display(Name = "Delay between emails per connection (ms)")]
    public int DelayBetweenEmailsMs { get; set; } = 0;

    public DateTime? UpdatedAt { get; set; }
}

public class TestSmtpViewModel
{
    [Required, EmailAddress]
    [Display(Name = "Send test email to")]
    public string ToEmail { get; set; } = string.Empty;
}
