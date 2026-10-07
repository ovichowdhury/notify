using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Notify.Test.TestSupport;
using Notify.Web.Models;
using Notify.Web.Services;

namespace Notify.Test.Services;

public class RecipientImportServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<Campaign> SeedCampaignAsync(CampaignStatus status = CampaignStatus.Draft)
    {
        await using var db = _db.CreateContext();
        var user = TestData.User();
        var campaign = TestData.Campaign(user.Id, status);
        db.Users.Add(user);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();
        return campaign;
    }

    private async Task<ImportResult> ImportAsync(Campaign campaign, string csv, string fileName = "list.csv")
    {
        await using var db = _db.CreateContext();
        var tracked = await db.Campaigns.SingleAsync(c => c.Id == campaign.Id);
        var service = new RecipientImportService(db, new RecipientFileParser());
        return await service.ImportAsync(tracked, TestData.FormFile(csv, fileName));
    }

    [Fact]
    public async Task Import_StoresRecipientsWithAllColumnsAsData()
    {
        var campaign = await SeedCampaignAsync();

        var result = await ImportAsync(campaign, "Email,Name,Plan\nalice@example.com,Alice,Gold\nbob@example.com,Bob,Silver\n");

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.SkippedInvalid);
        Assert.Equal(0, result.SkippedDuplicates);
        Assert.Equal(new[] { "Email", "Name", "Plan" }, result.Columns);

        await using var db = _db.CreateContext();
        var recipients = await db.CampaignRecipients.Where(r => r.CampaignId == campaign.Id).OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, recipients.Count);
        Assert.Equal("alice@example.com", recipients[0].Email);
        Assert.Equal("Alice", recipients[0].Name);
        Assert.All(recipients, r => Assert.Equal(RecipientStatus.Pending, r.Status));

        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(recipients[0].DataJson!)!;
        Assert.Equal("Gold", data["Plan"]);
        Assert.Equal("alice@example.com", data["Email"]);

        var saved = await db.Campaigns.SingleAsync(c => c.Id == campaign.Id);
        Assert.Equal("list.csv", saved.RecipientFileName);
        Assert.Equal(new[] { "Email", "Name", "Plan" }, JsonSerializer.Deserialize<List<string>>(saved.ColumnsJson!));
        Assert.Equal(CampaignStatus.Draft, saved.Status);
    }

    [Fact]
    public async Task Import_SkipsInvalidAndDuplicateEmails()
    {
        var campaign = await SeedCampaignAsync();

        var result = await ImportAsync(campaign,
            "Email,Name\nalice@example.com,Alice\nnot-an-email,Nobody\nALICE@example.com,Alice Again\n,Blank\nbob@example.com,Bob\n");

        Assert.Equal(2, result.Imported);
        Assert.Equal(2, result.SkippedInvalid);
        Assert.Equal(1, result.SkippedDuplicates);
    }

    [Fact]
    public async Task Import_ReplacesPreviousRecipientList()
    {
        var campaign = await SeedCampaignAsync();
        await ImportAsync(campaign, "Email\nold1@example.com\nold2@example.com\nold3@example.com\n");

        var result = await ImportAsync(campaign, "Email\nnew@example.com\n");

        Assert.Equal(1, result.Imported);
        await using var db = _db.CreateContext();
        var emails = await db.CampaignRecipients.Where(r => r.CampaignId == campaign.Id).Select(r => r.Email).ToListAsync();
        Assert.Equal(new[] { "new@example.com" }, emails);
    }

    [Theory]
    [InlineData(CampaignStatus.Completed)]
    [InlineData(CampaignStatus.Cancelled)]
    [InlineData(CampaignStatus.Failed)]
    public async Task Import_ResetsFinishedCampaignToDraft(CampaignStatus finished)
    {
        var campaign = await SeedCampaignAsync(finished);
        await using (var db = _db.CreateContext())
        {
            var c = await db.Campaigns.SingleAsync(x => x.Id == campaign.Id);
            c.StartedAt = DateTime.UtcNow.AddMinutes(-5);
            c.CompletedAt = DateTime.UtcNow;
            c.LastError = "boom";
            await db.SaveChangesAsync();
        }

        await ImportAsync(campaign, "Email\na@example.com\n");

        await using var verify = _db.CreateContext();
        var saved = await verify.Campaigns.SingleAsync(x => x.Id == campaign.Id);
        Assert.Equal(CampaignStatus.Draft, saved.Status);
        Assert.Null(saved.StartedAt);
        Assert.Null(saved.CompletedAt);
        Assert.Null(saved.LastError);
    }

    [Theory]
    [InlineData(CampaignStatus.Queued)]
    [InlineData(CampaignStatus.Running)]
    public async Task Import_RefusedWhileCampaignIsActive(CampaignStatus active)
    {
        var campaign = await SeedCampaignAsync(active);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(campaign, "Email\na@example.com\n"));
        Assert.Contains("running", ex.Message);
    }

    [Fact]
    public async Task Import_MapsDetectedNameColumnAndAlwaysExposesNamePlaceholder()
    {
        var campaign = await SeedCampaignAsync();
        await ImportAsync(campaign, "E-mail Address,Full Name\na@example.com,Alice Smith\n");

        await using var db = _db.CreateContext();
        var recipient = await db.CampaignRecipients.SingleAsync(r => r.CampaignId == campaign.Id);
        Assert.Equal("Alice Smith", recipient.Name);
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(recipient.DataJson!)!;
        Assert.Equal("Alice Smith", data["Name"]);
        Assert.Equal("Alice Smith", data["Full Name"]);
        Assert.Equal("a@example.com", data["Email"]);
    }

    [Fact]
    public async Task Import_UnsupportedFileLeavesExistingDataUntouched()
    {
        var campaign = await SeedCampaignAsync();
        await ImportAsync(campaign, "Email\nkeep@example.com\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(campaign, "whatever", "list.pdf"));

        await using var db = _db.CreateContext();
        Assert.Equal(1, await db.CampaignRecipients.CountAsync(r => r.CampaignId == campaign.Id));
        Assert.Equal("list.csv", (await db.Campaigns.SingleAsync(c => c.Id == campaign.Id)).RecipientFileName);
    }

    [Fact]
    public async Task Import_XlsxWorks()
    {
        var campaign = await SeedCampaignAsync();
        var bytes = TestData.Xlsx(
            new object?[] { "Email", "Name", "Discount" },
            new object?[] { "a@example.com", "Alice", 10 },
            new object?[] { "b@example.com", "Bob", 20 });

        await using var db = _db.CreateContext();
        var tracked = await db.Campaigns.SingleAsync(c => c.Id == campaign.Id);
        var service = new RecipientImportService(db, new RecipientFileParser());
        var result = await service.ImportAsync(tracked, TestData.FormFile(bytes, "list.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        Assert.Equal(2, result.Imported);
        Assert.Contains("Discount", result.Columns);
    }
}
