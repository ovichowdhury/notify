using Notify.Web.Services;

namespace Notify.Test.Unit;

public class CampaignQueueTests
{
    [Fact]
    public async Task Enqueue_ItemsAreReadInOrder()
    {
        var queue = new CampaignQueue();
        await queue.EnqueueAsync(3);
        await queue.EnqueueAsync(1);
        await queue.EnqueueAsync(2);

        Assert.Equal(3, await queue.Reader.ReadAsync());
        Assert.Equal(1, await queue.Reader.ReadAsync());
        Assert.Equal(2, await queue.Reader.ReadAsync());
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Reader_WaitsUntilAnItemArrives()
    {
        var queue = new CampaignQueue();
        var read = queue.Reader.ReadAsync().AsTask();
        Assert.False(read.IsCompleted);

        await queue.EnqueueAsync(42);
        Assert.Equal(42, await read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Enqueue_FromManyProducersLosesNothing()
    {
        var queue = new CampaignQueue();
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => queue.EnqueueAsync(i).AsTask()));

        var seen = new HashSet<int>();
        while (queue.Reader.TryRead(out var id)) seen.Add(id);
        Assert.Equal(200, seen.Count);
    }
}

public class CampaignRunRegistryTests
{
    [Fact]
    public void TryRegister_SecondRegistrationOfSameIdFails()
    {
        var registry = new CampaignRunRegistry();
        using var cts = new CancellationTokenSource();

        Assert.True(registry.TryRegister(1, cts));
        Assert.False(registry.TryRegister(1, new CancellationTokenSource()));
        Assert.True(registry.IsRunning(1));
    }

    [Fact]
    public void Cancel_UnknownIdReturnsFalse()
    {
        Assert.False(new CampaignRunRegistry().Cancel(99));
    }

    [Fact]
    public void Cancel_SignalsTheRegisteredToken()
    {
        var registry = new CampaignRunRegistry();
        using var cts = new CancellationTokenSource();
        registry.TryRegister(5, cts);

        Assert.True(registry.Cancel(5));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_DisposedTokenSourceReturnsFalse()
    {
        var registry = new CampaignRunRegistry();
        var cts = new CancellationTokenSource();
        registry.TryRegister(7, cts);
        cts.Dispose();

        Assert.False(registry.Cancel(7));
    }

    [Fact]
    public void Unregister_AllowsReRegistration()
    {
        var registry = new CampaignRunRegistry();
        registry.TryRegister(2, new CancellationTokenSource());
        registry.Unregister(2);

        Assert.False(registry.IsRunning(2));
        Assert.True(registry.TryRegister(2, new CancellationTokenSource()));
    }
}
