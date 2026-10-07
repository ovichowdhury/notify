using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Notify.Web.Models;

namespace Notify.Test.TestSupport;

public static class TestData
{
    public static ApplicationUser User(string? email = null, string company = "Test Co")
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        return new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            CompanyName = company
        };
    }

    public static SmtpSetting Smtp(string userId, int port, int concurrency = 2, string? username = null,
        string? passwordEncrypted = null, int delayMs = 0) => new()
    {
        UserId = userId,
        Host = "127.0.0.1",
        Port = port,
        Security = SmtpSecurity.None,
        Username = username,
        PasswordEncrypted = passwordEncrypted,
        FromEmail = "noreply@test.local",
        FromName = "Test Sender",
        ConcurrencyLevel = concurrency,
        DelayBetweenEmailsMs = delayMs
    };

    public static Campaign Campaign(string userId, CampaignStatus status = CampaignStatus.Queued,
        string subject = "Hi {{Name}}", string body = "<p>Hello {{Name}} ({{Email}})</p>") => new()
    {
        UserId = userId,
        Name = "Test campaign",
        Subject = subject,
        BodyHtml = body,
        Status = status
    };

    public static CampaignRecipient Recipient(int campaignId, string email, string? name = null,
        RecipientStatus status = RecipientStatus.Pending, IDictionary<string, string>? extra = null)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Email"] = email };
        if (name is not null) data["Name"] = name;
        if (extra is not null) foreach (var kv in extra) data[kv.Key] = kv.Value;
        return new CampaignRecipient
        {
            CampaignId = campaignId,
            Email = email,
            Name = name,
            Status = status,
            DataJson = JsonSerializer.Serialize(data)
        };
    }

    public static IFormFile FormFile(string content, string fileName, string contentType = "text/csv")
        => FormFile(Encoding.UTF8.GetBytes(content), fileName, contentType);

    public static IFormFile FormFile(byte[] bytes, string fileName, string contentType)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    /// <summary>Builds an .xlsx with the given rows (first row = header). Numbers are written as numbers.</summary>
    public static byte[] Xlsx(params object?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Recipients");
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
            {
                var value = rows[r][c];
                if (value is null) continue;
                if (value is int i) ws.Cell(r + 1, c + 1).Value = i;
                else if (value is double d) ws.Cell(r + 1, c + 1).Value = d;
                else ws.Cell(r + 1, c + 1).Value = value.ToString();
            }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static string Csv(int count, bool withPlan = true)
    {
        var sb = new StringBuilder(withPlan ? "Email,Name,Plan\n" : "Email,Name\n");
        for (var i = 1; i <= count; i++)
            sb.Append($"client{i}@example.com,Client {i}{(withPlan ? (i % 2 == 1 ? ",Gold" : ",Silver") : "")}\n");
        return sb.ToString();
    }
}
