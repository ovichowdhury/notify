using Notify.Web.Models;
using Notify.Web.Models.ViewModels;

namespace Notify.Test.Unit;

public class ModelTests
{
    [Theory]
    [InlineData(CampaignStatus.Draft, false)]
    [InlineData(CampaignStatus.Queued, true)]
    [InlineData(CampaignStatus.Running, true)]
    [InlineData(CampaignStatus.Completed, false)]
    [InlineData(CampaignStatus.Cancelled, false)]
    [InlineData(CampaignStatus.Failed, false)]
    public void Campaign_IsActive(CampaignStatus status, bool expected)
    {
        Assert.Equal(expected, new Campaign { Status = status }.IsActive);
    }

    [Fact]
    public void CampaignCounts_DerivedValues()
    {
        var counts = new CampaignCounts { Total = 10, Sent = 6, Failed = 2, Pending = 2 };
        Assert.Equal(8, counts.Processed);
        Assert.Equal(80, counts.ProgressPercent);
        Assert.Equal(75.0, counts.DeliveryRate);
    }

    [Fact]
    public void CampaignCounts_ZeroSafe()
    {
        var counts = new CampaignCounts();
        Assert.Equal(0, counts.ProgressPercent);
        Assert.Equal(0, counts.DeliveryRate);
    }

    [Fact]
    public void CampaignCounts_RoundsProgressAndRate()
    {
        var counts = new CampaignCounts { Total = 3, Sent = 1, Failed = 1, Pending = 1 };
        Assert.Equal(67, counts.ProgressPercent);
        Assert.Equal(50.0, counts.DeliveryRate);

        counts = new CampaignCounts { Total = 3, Sent = 2, Failed = 1 };
        Assert.Equal(66.7, counts.DeliveryRate);
    }

    [Fact]
    public void CampaignListItem_PendingIsRemainder()
    {
        var item = new CampaignListItemViewModel { Total = 10, Sent = 4, Failed = 1 };
        Assert.Equal(5, item.Pending);
    }

    [Fact]
    public void Defaults_AreSensible()
    {
        var smtp = new SmtpSetting();
        Assert.Equal(587, smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, smtp.Security);
        Assert.Equal(5, smtp.ConcurrencyLevel);
        Assert.Equal(0, smtp.DelayBetweenEmailsMs);

        Assert.Equal(CampaignStatus.Draft, new Campaign().Status);
        Assert.Equal(RecipientStatus.Pending, new CampaignRecipient().Status);
    }
}
