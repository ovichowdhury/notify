namespace Notify.Web.Constants;

/// <summary>Defaults and limits used by campaign screens.</summary>
public static class CampaignDefaults
{
    /// <summary>Starter HTML shown in the editor when a new campaign is created.</summary>
    public const string Template = """
<!DOCTYPE html>
<html>
<body style="font-family: Arial, sans-serif; color: #222; line-height: 1.5;">
  <h2>Hello {{Name}},</h2>
  <p>We have exciting news to share with you.</p>
  <p>Write your campaign content here. Any column from your recipients file can be used as a placeholder, for example {{Email}}.</p>
  <p>Best regards,<br/>Your Company</p>
</body>
</html>
""";

    /// <summary>Rows per page in the recipients table on the campaign details page.</summary>
    public const int RecipientsPageSize = 50;

    /// <summary>Placeholder values used by the preview when a campaign has no recipients yet.</summary>
    public const string SampleRecipientName = "Jane Doe";
    public const string SampleRecipientEmail = "jane@example.com";
}
