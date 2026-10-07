using Microsoft.AspNetCore.DataProtection;

namespace Notify.Web.Services;

/// <summary>Encrypts SMTP passwords at rest using ASP.NET Core Data Protection.</summary>
public class SmtpPasswordProtector
{
    private readonly IDataProtector _protector;

    public SmtpPasswordProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Notify.SmtpPassword.v1");
    }

    public string? Protect(string? plainText)
        => string.IsNullOrEmpty(plainText) ? null : _protector.Protect(plainText);

    public string? Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return null;
        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch
        {
            return null;
        }
    }
}
