namespace Notify.Web.Constants;

/// <summary>Rules for company logo uploads.</summary>
public static class LogoUpload
{
    public static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Folder under wwwroot where logos are stored.</summary>
    public const string Folder = "uploads/logos";
}
