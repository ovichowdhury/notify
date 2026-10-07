using System.Net;

namespace Notify.Test.Integration;

/// <summary>One tenant must never be able to see or act on another tenant's campaigns.</summary>
public class TenantIsolationTests(NotifyWebFactory factory) : IClassFixture<NotifyWebFactory>
{
    private async Task<(HttpClient Owner, HttpClient Other, int CampaignId)> SetupAsync()
    {
        var owner = factory.CreateBrowser();
        await owner.RegisterAsync(company: "Owner Co");
        var id = await owner.CreateCampaignAsync("Email,Name\nsecret@example.com,Secret\n", name: "Owner only campaign");

        var other = factory.CreateBrowser();
        await other.RegisterAsync(company: "Other Co");
        return (owner, other, id);
    }

    [Theory]
    [InlineData("Details")]
    [InlineData("Status")]
    [InlineData("Preview")]
    [InlineData("Export")]
    [InlineData("Edit")]
    public async Task Get_OtherTenantReceives404(string action)
    {
        var (_, other, id) = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Campaigns/{action}/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("Run")]
    [InlineData("Cancel")]
    [InlineData("RetryFailed")]
    [InlineData("Delete")]
    public async Task Post_OtherTenantReceives404AndNothingChanges(string action)
    {
        var (owner, other, id) = await SetupAsync();
        var token = await other.GetTokenAsync("/Campaigns/Create");

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostWithTokenAsync($"/Campaigns/{action}/{id}", token)).StatusCode);

        var status = await owner.StatusAsync(id);
        Assert.Equal("Draft", status.GetProperty("status").GetString());
        Assert.Equal(1, status.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Edit_PostByOtherTenantIs404()
    {
        var (owner, other, id) = await SetupAsync();
        var token = await other.GetTokenAsync("/Campaigns/Create");
        var response = await other.PostWithTokenAsync($"/Campaigns/Edit/{id}", token, ("Name", "Hijacked"), ("Subject", "x"), ("BodyHtml", "<p>x</p>"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Owner only campaign", await owner.GetOkAsync($"/Campaigns/Details/{id}"));
    }

    [Fact]
    public async Task Upload_ByOtherTenantIs404()
    {
        var (owner, other, id) = await SetupAsync();
        var token = await other.GetTokenAsync("/Campaigns/Create");
        using var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new ByteArrayContent("Email\nintruder@example.com\n"u8.ToArray()), "file", "r.csv" }
        };
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/Campaigns/Upload/{id}", content)).StatusCode);
        Assert.DoesNotContain("intruder@example.com", await owner.GetOkAsync($"/Campaigns/Details/{id}"));
    }

    [Fact]
    public async Task Lists_AndDashboardOnlyShowOwnCampaigns()
    {
        var (_, other, _) = await SetupAsync();
        Assert.DoesNotContain("Owner only campaign", await other.GetOkAsync("/Campaigns"));
        Assert.DoesNotContain("Owner only campaign", await other.GetOkAsync("/Dashboard"));
        Assert.DoesNotContain("Owner Co", await other.GetOkAsync("/Dashboard"));
    }
}
