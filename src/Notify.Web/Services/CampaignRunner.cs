using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Notify.Web.Data;
using Notify.Web.Models;

namespace Notify.Web.Services;

/// <summary>
/// Sends one campaign. Recipients are distributed over N worker tasks (N = the configured
/// concurrency level of the owning user); each worker owns a single SMTP connection and sends sequentially on it.
/// </summary>
public class CampaignRunner(
    IServiceScopeFactory scopeFactory,
    SmtpClientFactory smtpClientFactory,
    SmtpPasswordProtector passwordProtector,
    ILogger<CampaignRunner> logger)
{
    public const int MaxConcurrency = 50;

    private sealed record SendContext(
        int CampaignId,
        string Subject,
        string BodyHtml,
        SmtpSetting Smtp,
        string? Password);

    public async Task RunAsync(int campaignId, CancellationToken ct)
    {
        SendContext context;
        List<int> pendingIds;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
            if (campaign is null)
            {
                logger.LogWarning("Campaign {CampaignId} no longer exists; skipping.", campaignId);
                return;
            }

            if (campaign.Status != CampaignStatus.Queued)
            {
                // Cancelled (or otherwise changed) while waiting in the queue.
                logger.LogInformation("Campaign {CampaignId} is {Status}, not Queued; skipping.", campaignId, campaign.Status);
                return;
            }

            var smtp = await db.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == campaign.UserId, ct);
            if (smtp is null)
            {
                campaign.Status = CampaignStatus.Failed;
                campaign.LastError = "SMTP settings are not configured.";
                campaign.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            pendingIds = await db.CampaignRecipients
                .Where(r => r.CampaignId == campaignId && r.Status == RecipientStatus.Pending)
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .ToListAsync(ct);

            campaign.Status = CampaignStatus.Running;
            campaign.StartedAt = DateTime.UtcNow;
            campaign.CompletedAt = null;
            campaign.LastError = null;
            await db.SaveChangesAsync(ct);

            context = new SendContext(campaignId, campaign.Subject, campaign.BodyHtml, smtp, passwordProtector.Unprotect(smtp.PasswordEncrypted));
        }

        var workerCount = Math.Clamp(context.Smtp.ConcurrencyLevel, 1, MaxConcurrency);
        workerCount = Math.Min(workerCount, Math.Max(1, pendingIds.Count));
        logger.LogInformation("Campaign {CampaignId}: sending {Count} emails with {Workers} parallel connections.",
            campaignId, pendingIds.Count, workerCount);

        var channel = Channel.CreateUnbounded<int>();
        foreach (var id in pendingIds) channel.Writer.TryWrite(id);
        channel.Writer.Complete();

        var connectionErrors = new ConcurrentBag<string>();
        var cancelled = false;

        var workers = Enumerable.Range(0, workerCount)
            .Select(i => WorkerAsync(i, channel.Reader, context, connectionErrors, ct))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Campaign {CampaignId}: unexpected worker failure.", campaignId);
            connectionErrors.Add(ex.Message);
        }

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, CancellationToken.None);
            if (campaign is null) return;

            var remaining = await db.CampaignRecipients.CountAsync(
                r => r.CampaignId == campaignId && r.Status == RecipientStatus.Pending, CancellationToken.None);

            campaign.CompletedAt = DateTime.UtcNow;
            if (cancelled)
            {
                campaign.Status = CampaignStatus.Cancelled;
                campaign.LastError = "Cancelled by user.";
            }
            else if (remaining > 0 && !connectionErrors.IsEmpty)
            {
                campaign.Status = CampaignStatus.Failed;
                campaign.LastError = Truncate("Could not connect to SMTP server: " + connectionErrors.First(), 1000);
            }
            else
            {
                campaign.Status = CampaignStatus.Completed;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Campaign {CampaignId} finished with status {Status}.", campaignId, campaign.Status);
        }
    }

    private async Task WorkerAsync(
        int workerIndex,
        ChannelReader<int> reader,
        SendContext context,
        ConcurrentBag<string> connectionErrors,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        SmtpClient client;
        try
        {
            client = await smtpClientFactory.CreateConnectedAsync(context.Smtp, context.Password, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Campaign {CampaignId} worker {Worker}: could not connect to SMTP.", context.CampaignId, workerIndex);
            connectionErrors.Add(ex.Message);
            return;
        }

        var consecutiveConnectionFailures = 0;
        var delay = Math.Clamp(context.Smtp.DelayBetweenEmailsMs, 0, 60_000);
        var requiresAuth = !string.IsNullOrWhiteSpace(context.Smtp.Username);

        try
        {
            await foreach (var recipientId in reader.ReadAllAsync(ct))
            {
                var recipient = await db.CampaignRecipients.FirstOrDefaultAsync(r => r.Id == recipientId, ct);
                if (recipient is null) continue;

                try
                {
                    if (!client.IsConnected || (requiresAuth && !client.IsAuthenticated))
                    {
                        client.Dispose();
                        client = await smtpClientFactory.CreateConnectedAsync(context.Smtp, context.Password, ct);
                    }

                    var data = Deserialize(recipient);
                    var subject = TemplateRenderer.Render(context.Subject, data, htmlEncode: false);
                    var html = TemplateRenderer.Render(context.BodyHtml, data, htmlEncode: true);
                    var message = SmtpClientFactory.BuildMessage(context.Smtp, recipient.Email, recipient.Name, subject, html);

                    await client.SendAsync(message, ct);

                    recipient.Status = RecipientStatus.Sent;
                    recipient.SentAt = DateTime.UtcNow;
                    recipient.Error = null;
                    consecutiveConnectionFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    recipient.Status = RecipientStatus.Failed;
                    recipient.Error = Truncate(ex.Message, 1000);
                    logger.LogWarning(ex, "Campaign {CampaignId}: failed to send to {Email}.", context.CampaignId, recipient.Email);

                    if (IsConnectionProblem(ex))
                    {
                        consecutiveConnectionFailures++;
                        try { await client.DisconnectAsync(true, CancellationToken.None); } catch { /* ignore */ }
                    }
                }

                recipient.Attempts++;
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();

                if (consecutiveConnectionFailures >= 3)
                {
                    connectionErrors.Add("Lost connection to the SMTP server repeatedly.");
                    logger.LogWarning("Campaign {CampaignId} worker {Worker}: giving up after repeated connection failures.", context.CampaignId, workerIndex);
                    return;
                }

                if (delay > 0) await Task.Delay(delay, ct);
            }
        }
        finally
        {
            try { await client.DisconnectAsync(true, CancellationToken.None); } catch { /* ignore */ }
            client.Dispose();
        }
    }

    public static IReadOnlyDictionary<string, string> Deserialize(CampaignRecipient recipient)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(recipient.DataJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(recipient.DataJson);
                if (parsed is not null)
                    foreach (var kv in parsed) data[kv.Key] = kv.Value;
            }
            catch { /* fall through to defaults */ }
        }
        data.TryAdd("Email", recipient.Email);
        data.TryAdd("Name", recipient.Name ?? string.Empty);
        return data;
    }

    private static bool IsConnectionProblem(Exception ex) => ex switch
    {
        IOException => true,
        SocketException => true,
        ServiceNotConnectedException => true,
        ServiceNotAuthenticatedException => true,
        AuthenticationException => true,
        SmtpProtocolException => true,
        SmtpCommandException sce => sce.StatusCode == SmtpStatusCode.ServiceNotAvailable,
        _ => false
    };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
