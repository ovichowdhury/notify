using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Notify.Web.Services;

/// <summary>In-process queue of campaign ids waiting to be sent.</summary>
public class CampaignQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(int campaignId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(campaignId, ct);

    public ChannelReader<int> Reader => _channel.Reader;
}

/// <summary>Tracks campaigns currently being sent so they can be cancelled from the UI.</summary>
public class CampaignRunRegistry
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    public bool TryRegister(int campaignId, CancellationTokenSource cts) => _running.TryAdd(campaignId, cts);

    public void Unregister(int campaignId) => _running.TryRemove(campaignId, out _);

    public bool IsRunning(int campaignId) => _running.ContainsKey(campaignId);

    public bool Cancel(int campaignId)
    {
        if (!_running.TryGetValue(campaignId, out var cts)) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
