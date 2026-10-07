using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notify.Test.TestSupport;
using Notify.Web.Models;
using Notify.Web.Services;

namespace Notify.Test.Services;

public class CampaignSenderServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ServiceProvider _services;

    public CampaignSenderServiceTests() => _services = _db.BuildServices();

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private CampaignSenderService CreateService() => new(
        _services.GetRequiredService<CampaignQueue>(),
        _services.GetRequiredService<CampaignRunRegistry>(),
        _services.GetRequiredService<CampaignRunner>(),
        _services.GetRequiredService<IServiceScopeFactory>(),
        _services.GetRequiredService<ILogger<CampaignSenderService>>());

    private async Task<Campaign> SeedAsync(CampaignStatus status, int? smtpPort, int recipients = 2)
    {
        await using var db = _db.CreateContext();
        var user = TestData.User();
        db.Users.Add(user);
        if (smtpPort is not null) db.SmtpSettings.Add(TestData.Smtp(user.Id, smtpPort.Value, 1));
        var campaign = TestData.Campaign(user.Id, status);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync();
        for (var i = 0; i < recipients; i++)
            db.CampaignRecipients.Add(TestData.Recipient(campaign.Id, $"r{i}@example.com", $"R{i}"));
        await db.SaveChangesAsync();
        return campaign;
    }

    private async Task<Campaign> WaitForFinalStatusAsync(int id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            await using var db = _db.CreateContext();
            var c = await db.Campaigns.AsNoTracking().SingleAsync(x => x.Id == id);
            if (!c.IsActive) return c;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Campaign {id} did not finish.");
    }

    [Fact]
    public async Task ProcessesCampaignsPushedToTheQueue()
    {
        await using var server = new FakeSmtpServer();
        var campaign = await SeedAsync(CampaignStatus.Queued, server.Port);
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await _services.GetRequiredService<CampaignQueue>().EnqueueAsync(campaign.Id);
            var finished = await WaitForFinalStatusAsync(campaign.Id);

            Assert.Equal(CampaignStatus.Completed, finished.Status);
            Assert.Equal(2, server.Messages.Count);
            Assert.False(_services.GetRequiredService<CampaignRunRegistry>().IsRunning(campaign.Id));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(CampaignStatus.Running)]
    [InlineData(CampaignStatus.Queued)]
    public async Task OnStartup_ReQueuesInterruptedCampaigns(CampaignStatus interrupted)
    {
        await using var server = new FakeSmtpServer();
        var campaign = await SeedAsync(interrupted, server.Port);
        var untouched = await SeedAsync(CampaignStatus.Draft, server.Port);

        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var finished = await WaitForFinalStatusAsync(campaign.Id);
            Assert.Equal(CampaignStatus.Completed, finished.Status);
            Assert.Equal(2, server.Messages.Count);

            await using var db = _db.CreateContext();
            Assert.Equal(CampaignStatus.Draft, (await db.Campaigns.SingleAsync(c => c.Id == untouched.Id)).Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InterruptedCampaignWithoutSmtpEndsUpFailedNotStuck()
    {
        var campaign = await SeedAsync(CampaignStatus.Running, smtpPort: null);
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var finished = await WaitForFinalStatusAsync(campaign.Id);
            Assert.Equal(CampaignStatus.Failed, finished.Status);
            Assert.Equal("SMTP settings are not configured.", finished.LastError);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_CancelsARunningCampaignAndShutsDownCleanly()
    {
        await using var server = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(300) };
        var campaign = await SeedAsync(CampaignStatus.Queued, server.Port, recipients: 20);
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await _services.GetRequiredService<CampaignQueue>().EnqueueAsync(campaign.Id);

        // Wait until the first message is through so we know the runner is active.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.Messages.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(server.Messages.IsEmpty);

        await service.StopAsync(CancellationToken.None);

        var finished = await WaitForFinalStatusAsync(campaign.Id);
        Assert.Equal(CampaignStatus.Cancelled, finished.Status);
        Assert.True(server.Messages.Count < 20);
    }

    [Fact]
    public async Task UiCancelThroughRegistryStopsTheCampaign()
    {
        await using var server = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(300) };
        var campaign = await SeedAsync(CampaignStatus.Queued, server.Port, recipients: 20);
        var registry = _services.GetRequiredService<CampaignRunRegistry>();
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await _services.GetRequiredService<CampaignQueue>().EnqueueAsync(campaign.Id);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!registry.IsRunning(campaign.Id) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(registry.IsRunning(campaign.Id));

            Assert.True(registry.Cancel(campaign.Id));
            var finished = await WaitForFinalStatusAsync(campaign.Id);

            Assert.Equal(CampaignStatus.Cancelled, finished.Status);
            Assert.Equal("Cancelled by user.", finished.LastError);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
