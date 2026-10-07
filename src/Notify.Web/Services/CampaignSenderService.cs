using Microsoft.EntityFrameworkCore;
using Notify.Web.Data;
using Notify.Web.Models;

namespace Notify.Web.Services;

/// <summary>
/// Background service that picks queued campaigns and runs them. Campaigns of different users
/// run concurrently; the per-campaign parallelism comes from the SMTP concurrency level of the owner.
/// </summary>
public class CampaignSenderService(
    CampaignQueue queue,
    CampaignRunRegistry registry,
    CampaignRunner runner,
    IServiceScopeFactory scopeFactory,
    ILogger<CampaignSenderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverInterruptedCampaignsAsync(stoppingToken);

            await foreach (var campaignId in queue.Reader.ReadAllAsync(stoppingToken))
            {
                _ = RunTrackedAsync(campaignId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunTrackedAsync(int campaignId, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!registry.TryRegister(campaignId, cts))
        {
            logger.LogWarning("Campaign {CampaignId} is already running.", campaignId);
            return;
        }

        try
        {
            await runner.RunAsync(campaignId, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Campaign {CampaignId} crashed.", campaignId);
            await MarkFailedAsync(campaignId, ex.Message);
        }
        finally
        {
            registry.Unregister(campaignId);
        }
    }

    private async Task MarkFailedAsync(int campaignId, string error)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null) return;
            campaign.Status = CampaignStatus.Failed;
            campaign.LastError = error.Length > 1000 ? error[..1000] : error;
            campaign.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark campaign {CampaignId} as failed.", campaignId);
        }
    }

    /// <summary>Campaigns left Running/Queued by a previous process are re-queued; only pending recipients are sent.</summary>
    private async Task RecoverInterruptedCampaignsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var interrupted = await db.Campaigns
                .Where(c => c.Status == CampaignStatus.Running || c.Status == CampaignStatus.Queued)
                .ToListAsync(ct);

            foreach (var campaign in interrupted)
            {
                campaign.Status = CampaignStatus.Queued;
                campaign.LastError = "Resumed after application restart.";
            }
            await db.SaveChangesAsync(ct);

            foreach (var campaign in interrupted)
            {
                logger.LogInformation("Re-queuing interrupted campaign {CampaignId}.", campaign.Id);
                await queue.EnqueueAsync(campaign.Id, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover interrupted campaigns.");
        }
    }
}
