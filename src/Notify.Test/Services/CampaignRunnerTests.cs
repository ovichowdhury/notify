using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notify.Test.TestSupport;
using Notify.Web.Data;
using Notify.Web.Models;
using Notify.Web.Services;

namespace Notify.Test.Services;

public class CampaignRunnerTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ServiceProvider _services;

    public CampaignRunnerTests() => _services = _db.BuildServices();

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private CampaignRunner Runner => _services.GetRequiredService<CampaignRunner>();
    private SmtpPasswordProtector Protector => _services.GetRequiredService<SmtpPasswordProtector>();

    private async Task<Campaign> SeedAsync(int recipients, int? smtpPort, int concurrency = 2,
        CampaignStatus status = CampaignStatus.Queued, Action<SmtpSetting>? smtp = null, int alreadySent = 0)
    {
        await using var db = _db.CreateContext();
        var user = TestData.User();
        db.Users.Add(user);
        if (smtpPort is not null)
        {
            var setting = TestData.Smtp(user.Id, smtpPort.Value, concurrency);
            smtp?.Invoke(setting);
            db.SmtpSettings.Add(setting);
        }
        var campaign = TestData.Campaign(user.Id, status);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();

        for (var i = 1; i <= recipients; i++)
            db.CampaignRecipients.Add(TestData.Recipient(campaign.Id, $"client{i}@example.com", $"Client {i}",
                i <= alreadySent ? RecipientStatus.Sent : RecipientStatus.Pending));
        await db.SaveChangesAsync();
        return campaign;
    }

    private async Task<(Campaign Campaign, List<CampaignRecipient> Recipients)> LoadAsync(int campaignId)
    {
        await using var db = _db.CreateContext();
        var campaign = await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        var recipients = await db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == campaignId).OrderBy(r => r.Id).ToListAsync();
        return (campaign, recipients);
    }

    [Fact]
    public async Task Run_SendsEveryPendingRecipientInParallelAndCompletes()
    {
        await using var server = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(80) };
        var campaign = await SeedAsync(recipients: 20, smtpPort: server.Port, concurrency: 4);

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Completed, saved.Status);
        Assert.NotNull(saved.StartedAt);
        Assert.NotNull(saved.CompletedAt);
        Assert.Null(saved.LastError);
        Assert.All(recipients, r =>
        {
            Assert.Equal(RecipientStatus.Sent, r.Status);
            Assert.NotNull(r.SentAt);
            Assert.Equal(1, r.Attempts);
            Assert.Null(r.Error);
        });
        Assert.Equal(20, server.Messages.Count);
        Assert.Equal(4, server.PeakConcurrentConnections);
        Assert.Equal(4, server.ConnectionsAccepted);
    }

    [Fact]
    public async Task Run_RendersSubjectAndHtmlEncodedBodyPerRecipient()
    {
        await using var server = new FakeSmtpServer();
        var campaign = await SeedAsync(recipients: 0, smtpPort: server.Port, concurrency: 1);
        await using (var db = _db.CreateContext())
        {
            db.CampaignRecipients.Add(TestData.Recipient(campaign.Id, "x@example.com", "<b>Xavier</b>", extra: new Dictionary<string, string> { ["Plan"] = "Gold & Co" }));
            var c = await db.Campaigns.SingleAsync(x => x.Id == campaign.Id);
            c.BodyHtml = "<p>Dear {{Name}}, plan {{plan}}.</p>";
            await db.SaveChangesAsync();
        }

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var message = Assert.Single(server.Messages);
        Assert.Equal("x@example.com", Assert.Single(message.Recipients));
        Assert.Equal("noreply@test.local", message.From);
        Assert.Contains("Dear &lt;b&gt;Xavier&lt;/b&gt;, plan Gold &amp; Co.", message.Data);
        Assert.Contains("Subject: Hi <b>Xavier</b>", message.Data);
        Assert.Contains("From: Test Sender <noreply@test.local>", message.Data);
    }

    [Fact]
    public async Task Run_WithoutSmtpSettingsFailsImmediately()
    {
        var campaign = await SeedAsync(recipients: 3, smtpPort: null);

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Failed, saved.Status);
        Assert.Equal("SMTP settings are not configured.", saved.LastError);
        Assert.NotNull(saved.CompletedAt);
        Assert.All(recipients, r => Assert.Equal(RecipientStatus.Pending, r.Status));
    }

    [Theory]
    [InlineData(CampaignStatus.Draft)]
    [InlineData(CampaignStatus.Cancelled)]
    [InlineData(CampaignStatus.Completed)]
    public async Task Run_SkipsCampaignsThatAreNotQueued(CampaignStatus status)
    {
        await using var server = new FakeSmtpServer();
        var campaign = await SeedAsync(recipients: 2, smtpPort: server.Port, status: status);

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(status, saved.Status);
        Assert.Empty(server.Messages);
        Assert.All(recipients, r => Assert.Equal(RecipientStatus.Pending, r.Status));
    }

    [Fact]
    public async Task Run_UnknownCampaignIsIgnored()
    {
        await Runner.RunAsync(123456, CancellationToken.None);
    }

    [Fact]
    public async Task Run_UnreachableServerMarksCampaignFailedAndKeepsRecipientsPending()
    {
        var campaign = await SeedAsync(recipients: 3, smtpPort: FakeSmtpServer.ClosedPort(), concurrency: 2);

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Failed, saved.Status);
        Assert.StartsWith("Could not connect to SMTP server:", saved.LastError);
        Assert.All(recipients, r => Assert.Equal(RecipientStatus.Pending, r.Status));
    }

    [Fact]
    public async Task Run_RejectedRecipientIsMarkedFailedOthersSent()
    {
        await using var server = new FakeSmtpServer { RejectRecipient = a => a.StartsWith("client2@") };
        var campaign = await SeedAsync(recipients: 3, smtpPort: server.Port, concurrency: 1);

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Completed, saved.Status);
        Assert.Equal(RecipientStatus.Sent, recipients[0].Status);
        Assert.Equal(RecipientStatus.Failed, recipients[1].Status);
        Assert.Contains("User unknown", recipients[1].Error);
        Assert.Equal(1, recipients[1].Attempts);
        Assert.Equal(RecipientStatus.Sent, recipients[2].Status);
        Assert.Equal(2, server.Messages.Count);
    }

    [Fact]
    public async Task Run_CancellationStopsBetweenMessagesAndLeavesRestPending()
    {
        await using var server = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(250) };
        var campaign = await SeedAsync(recipients: 10, smtpPort: server.Port, concurrency: 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await Runner.RunAsync(campaign.Id, cts.Token);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Cancelled, saved.Status);
        Assert.Equal("Cancelled by user.", saved.LastError);
        Assert.Contains(recipients, r => r.Status == RecipientStatus.Sent);
        Assert.Contains(recipients, r => r.Status == RecipientStatus.Pending);
        Assert.DoesNotContain(recipients, r => r.Status == RecipientStatus.Failed);
    }

    [Fact]
    public async Task Run_OnlySendsPendingRecipientsSoReRunResumes()
    {
        await using var server = new FakeSmtpServer();
        var campaign = await SeedAsync(recipients: 5, smtpPort: server.Port, alreadySent: 3);

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Completed, saved.Status);
        Assert.Equal(2, server.Messages.Count);
        Assert.All(recipients, r => Assert.Equal(RecipientStatus.Sent, r.Status));
        Assert.Equal(0, recipients[0].Attempts); // untouched
        Assert.Equal(1, recipients[4].Attempts);
    }

    [Fact]
    public async Task Run_AuthenticatesWithDecryptedPassword()
    {
        await using var server = new FakeSmtpServer { RequiredCredentials = ("mailer", "pw") };
        var campaign = await SeedAsync(recipients: 2, smtpPort: server.Port, concurrency: 1, smtp: s =>
        {
            s.Username = "mailer";
            s.PasswordEncrypted = Protector.Protect("pw");
        });

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, _) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Completed, saved.Status);
        Assert.Equal(2, server.Messages.Count);
    }

    [Fact]
    public async Task Run_WrongPasswordFailsCampaignWithConnectionError()
    {
        await using var server = new FakeSmtpServer { RequiredCredentials = ("mailer", "pw") };
        var campaign = await SeedAsync(recipients: 2, smtpPort: server.Port, concurrency: 1, smtp: s =>
        {
            s.Username = "mailer";
            s.PasswordEncrypted = Protector.Protect("wrong");
        });

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        var (saved, recipients) = await LoadAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Failed, saved.Status);
        Assert.Contains("Authentication failed", saved.LastError);
        Assert.All(recipients, r => Assert.Equal(RecipientStatus.Pending, r.Status));
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task Run_ConcurrencyIsClampedToAtLeastOneAndAtMostRecipients()
    {
        await using var server = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(50) };
        var campaign = await SeedAsync(recipients: 2, smtpPort: server.Port, concurrency: 0);

        await Runner.RunAsync(campaign.Id, CancellationToken.None);

        Assert.Equal(1, server.PeakConcurrentConnections);
        Assert.Equal(2, server.Messages.Count);

        await using var server2 = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(50) };
        var campaign2 = await SeedAsync(recipients: 3, smtpPort: server2.Port, concurrency: 50);
        await Runner.RunAsync(campaign2.Id, CancellationToken.None);
        Assert.Equal(3, server2.ConnectionsAccepted); // never more workers than recipients
    }

    [Fact]
    public async Task Run_DelayBetweenEmailsIsApplied()
    {
        await using var server = new FakeSmtpServer();
        var campaign = await SeedAsync(recipients: 3, smtpPort: server.Port, concurrency: 1, smtp: s => s.DelayBetweenEmailsMs = 200);

        var sw = Stopwatch.StartNew();
        await Runner.RunAsync(campaign.Id, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 550, $"expected at least ~600 ms, took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(3, server.Messages.Count);
    }

    [Fact]
    public void Deserialize_ReadsDataJsonCaseInsensitivelyAndFillsDefaults()
    {
        var recipient = new CampaignRecipient { Email = "a@example.com", Name = "Alice", DataJson = "{\"plan\":\"Gold\"}" };
        var data = CampaignRunner.Deserialize(recipient);

        Assert.Equal("Gold", data["PLAN"]);
        Assert.Equal("a@example.com", data["email"]);
        Assert.Equal("Alice", data["name"]);
    }

    [Fact]
    public void Deserialize_ToleratesMissingOrInvalidJson()
    {
        var noJson = CampaignRunner.Deserialize(new CampaignRecipient { Email = "a@example.com" });
        Assert.Equal("a@example.com", noJson["Email"]);
        Assert.Equal(string.Empty, noJson["Name"]);

        var badJson = CampaignRunner.Deserialize(new CampaignRecipient { Email = "b@example.com", Name = "B", DataJson = "{not json" });
        Assert.Equal("b@example.com", badJson["Email"]);
        Assert.Equal("B", badJson["Name"]);
    }

    [Fact]
    public void Deserialize_DataJsonWinsOverColumnsExceptWhenAbsent()
    {
        var recipient = new CampaignRecipient { Email = "col@example.com", Name = "Column", DataJson = "{\"Email\":\"json@example.com\"}" };
        var data = CampaignRunner.Deserialize(recipient);
        Assert.Equal("json@example.com", data["Email"]);
        Assert.Equal("Column", data["Name"]);
    }
}
