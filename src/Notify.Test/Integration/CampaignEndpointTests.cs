using System.Net;
using Notify.Test.TestSupport;

namespace Notify.Test.Integration;

public class CampaignEndpointTests(NotifyWebFactory factory) : IClassFixture<NotifyWebFactory>
{
    private const string Csv = "Email,Name,Plan\nalice@example.com,Alice,Gold\nbob@example.com,Bob,Silver\n";

    private async Task<HttpClient> TenantAsync()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        return client;
    }

    // ---------- list / create ----------

    [Fact]
    public async Task Index_EmptyStateThenListsCampaigns()
    {
        var client = await TenantAsync();
        Assert.Contains("No campaigns yet", await client.GetOkAsync("/Campaigns"));

        var id = await client.CreateCampaignAsync(Csv, name: "Spring promo");
        var list = await client.GetOkAsync("/Campaigns");
        Assert.Contains("Spring promo", list);
        Assert.Contains($"/Campaigns/Details/{id}", list);
    }

    [Fact]
    public async Task Create_GetShowsDefaultTemplate()
    {
        var client = await TenantAsync();
        var html = await client.GetOkAsync("/Campaigns/Create");
        Assert.Contains("Hello {{Name}}", html);
        Assert.Contains("data-placeholder=\"Email\"", html);
    }

    [Fact]
    public async Task Create_WithoutFileCreatesDraftAndAsksForUpload()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(null);
        var details = await client.GetOkAsync($"/Campaigns/Details/{id}");
        Assert.Contains("Campaign created. Upload a recipients file to continue.", details);
        Assert.Contains("Draft", details);
        Assert.Contains("No recipients. Upload a file to add some.", details);
    }

    [Fact]
    public async Task Create_WithCsvImportsRecipientsAndExposesColumns()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv);

        var details = await client.GetOkAsync($"/Campaigns/Details/{id}");
        Assert.Contains("Imported 2 recipient(s).", details);
        Assert.Contains("{{Plan}}", details);
        Assert.Contains("alice@example.com", details);

        var status = await client.StatusAsync(id);
        Assert.Equal("Draft", status.GetProperty("status").GetString());
        Assert.Equal(2, status.GetProperty("total").GetInt32());
        Assert.Equal(2, status.GetProperty("pending").GetInt32());
    }

    [Fact]
    public async Task Create_WithXlsxWorks()
    {
        var client = await TenantAsync();
        var bytes = TestData.Xlsx(new object?[] { "Email", "Name" }, new object?[] { "x@example.com", "X" });
        var id = await client.CreateCampaignAsync(null, fileBytes: bytes, fileName: "list.xlsx",
            contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        Assert.Contains("Imported 1 recipient(s).", await client.GetOkAsync($"/Campaigns/Details/{id}"));
    }

    [Fact]
    public async Task Create_InvalidModelReturnsFormWithErrors()
    {
        var client = await TenantAsync();
        var token = await client.GetTokenAsync("/Campaigns/Create");
        var response = await client.PostWithTokenAsync("/Campaigns/Create", token, ("Name", ""), ("Subject", ""), ("BodyHtml", ""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("field-validation-error", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_UnsupportedFileStillCreatesCampaignButReportsImportError()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync("garbage", fileName: "list.pdf", contentType: "application/pdf");
        var details = await client.GetOkAsync($"/Campaigns/Details/{id}");
        Assert.Contains("could not be imported", details);
        Assert.Contains("Unsupported file type", details);
    }

    // ---------- details / preview / export ----------

    [Fact]
    public async Task Preview_RendersTemplateWithFirstRecipientOrSampleData()
    {
        var client = await TenantAsync();
        var withData = await client.CreateCampaignAsync(Csv, body: "<p>Dear {{Name}}, {{Plan}}</p>");
        Assert.Equal("<p>Dear Alice, Gold</p>", await client.GetOkAsync($"/Campaigns/Preview/{withData}"));

        var empty = await client.CreateCampaignAsync(null, body: "<p>Dear {{Name}}</p>");
        Assert.Equal("<p>Dear Jane Doe</p>", await client.GetOkAsync($"/Campaigns/Preview/{empty}"));
    }

    [Fact]
    public async Task Details_WarnsAboutPlaceholdersMissingFromFile()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv, body: "<p>{{Name}} {{Discount}}</p>");
        var details = await client.GetOkAsync($"/Campaigns/Details/{id}");
        Assert.Contains("will render empty", details);
        Assert.Contains("{{Discount}}", details);
    }

    [Fact]
    public async Task Details_FilterAndPagingParametersAreAccepted()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv);
        Assert.Contains("No recipients with this status.", await client.GetOkAsync($"/Campaigns/Details/{id}?status=Sent"));
        Assert.Contains("alice@example.com", await client.GetOkAsync($"/Campaigns/Details/{id}?status=Pending&page=99"));
    }

    [Fact]
    public async Task Export_ReturnsCsvWithOptionalStatusFilter()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv);

        var response = await client.GetAsync($"/Campaigns/Export/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("Email,Name,Status,SentAt,Attempts,Error", csv);
        Assert.Contains("alice@example.com,Alice,Pending", csv);

        var sentOnly = await client.GetOkAsync($"/Campaigns/Export/{id}?status=Sent");
        Assert.DoesNotContain("alice@example.com", sentOnly);
    }

    [Theory]
    [InlineData("/Campaigns/Details/999999")]
    [InlineData("/Campaigns/Status/999999")]
    [InlineData("/Campaigns/Preview/999999")]
    [InlineData("/Campaigns/Export/999999")]
    [InlineData("/Campaigns/Edit/999999")]
    public async Task UnknownCampaignIs404(string path)
    {
        var client = await TenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    // ---------- edit / upload ----------

    [Fact]
    public async Task Edit_UpdatesFieldsAndOptionallyReplacesRecipients()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv, name: "Old name");

        var form = await client.GetOkAsync($"/Campaigns/Edit/{id}");
        Assert.Contains("value=\"Old name\"", form);
        Assert.Contains("{{Plan}}", form); // placeholder chips from the uploaded file

        var response = await client.PostFormAsync($"/Campaigns/Edit/{id}", $"/Campaigns/Edit/{id}",
            ("Name", "New name"), ("Subject", "New subject"), ("BodyHtml", "<p>New</p>"));
        var details = await client.FollowAsync(response);
        Assert.Contains("Campaign saved.", details);
        Assert.Contains("New name", details);
        Assert.Contains("New subject", details);

        var upload = await client.UploadAsync(id, "Email\nonly@example.com\n");
        var after = await client.FollowAsync(upload);
        Assert.Contains("Imported 1 recipient(s).", after);
        Assert.DoesNotContain("alice@example.com", after);
    }

    [Fact]
    public async Task Upload_WithoutFileShowsError()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(null);
        var response = await client.UploadAsync(id, null);
        Assert.Contains("Choose a .csv or .xlsx file to upload.", await client.FollowAsync(response));
    }

    // ---------- run / cancel / retry / delete ----------

    [Fact]
    public async Task Run_WithoutSmtpRedirectsToSettings()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv);
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));

        var response = await client.PostWithTokenAsync($"/Campaigns/Run/{id}", token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Settings/Smtp", response.Headers.Location?.ToString());
        Assert.Contains("Configure your SMTP settings before running a campaign.", await client.GetOkAsync("/Settings/Smtp"));
    }

    [Fact]
    public async Task Run_WithoutPendingRecipientsShowsError()
    {
        await using var server = new FakeSmtpServer();
        var client = await TenantAsync();
        await client.SaveSmtpAsync(server.Port);
        var id = await client.CreateCampaignAsync(null);
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));

        var response = await client.PostWithTokenAsync($"/Campaigns/Run/{id}", token);
        Assert.Contains("There are no pending recipients to send to.", await client.FollowAsync(response));
    }

    [Fact]
    public async Task Run_SendsCampaignThroughBackgroundServiceAndReportsResults()
    {
        await using var server = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(40) };
        var client = await TenantAsync();
        await client.SaveSmtpAsync(server.Port, concurrency: 3);
        var id = await client.CreateCampaignAsync(TestData.Csv(12), subject: "Hello {{Name}}", body: "<p>{{Plan}} for {{Email}}</p>");
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));

        var run = await client.PostWithTokenAsync($"/Campaigns/Run/{id}", token);
        Assert.Contains("Campaign queued. Sending to 12 recipient(s).", await client.FollowAsync(run));

        var status = await client.WaitForCompletionAsync(id);
        Assert.Equal("Completed", status.GetProperty("status").GetString());
        Assert.Equal(12, status.GetProperty("sent").GetInt32());
        Assert.Equal(0, status.GetProperty("failed").GetInt32());
        Assert.Equal(100, status.GetProperty("progressPercent").GetInt32());
        Assert.Equal(100, status.GetProperty("deliveryRate").GetDouble());
        Assert.Equal(12, server.Messages.Count);
        Assert.Equal(3, server.PeakConcurrentConnections);

        var details = await client.GetOkAsync($"/Campaigns/Details/{id}");
        Assert.Contains("Completed", details);
        Assert.Contains("Delivered</span>", details);

        var dashboard = await client.GetOkAsync("/Dashboard");
        Assert.Contains(">12<", dashboard); // delivered stat
    }

    [Fact]
    public async Task Run_FailedRecipientsCanBeRetried()
    {
        await using var server = new FakeSmtpServer { RejectRecipient = a => a.StartsWith("bob@") };
        var client = await TenantAsync();
        await client.SaveSmtpAsync(server.Port, concurrency: 1);
        var id = await client.CreateCampaignAsync(Csv);
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));

        await client.PostWithTokenAsync($"/Campaigns/Run/{id}", token);
        var first = await client.WaitForCompletionAsync(id);
        Assert.Equal(1, first.GetProperty("sent").GetInt32());
        Assert.Equal(1, first.GetProperty("failed").GetInt32());

        var details = await client.GetOkAsync($"/Campaigns/Details/{id}");
        Assert.Contains("User unknown", details);
        Assert.Contains("Retry failed", details);

        var failedCsv = await client.GetOkAsync($"/Campaigns/Export/{id}?status=Failed");
        Assert.Contains("bob@example.com,Bob,Failed", failedCsv);

        server.RejectRecipient = null;
        token = ClientExtensions.ExtractToken(details);
        var retry = await client.PostWithTokenAsync($"/Campaigns/RetryFailed/{id}", token);
        Assert.Contains("Retrying 1 failed recipient(s).", await client.FollowAsync(retry));

        var second = await client.WaitForCompletionAsync(id);
        Assert.Equal(2, second.GetProperty("sent").GetInt32());
        Assert.Equal(0, second.GetProperty("failed").GetInt32());
        Assert.Contains("bob@example.com,Bob,Sent,", await client.GetOkAsync($"/Campaigns/Export/{id}"));
        Assert.Contains(",2,", await client.GetOkAsync($"/Campaigns/Export/{id}?status=Sent")); // attempts = 2
    }

    [Fact]
    public async Task RetryFailed_WithNothingFailedShowsError()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv);
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));
        var response = await client.PostWithTokenAsync($"/Campaigns/RetryFailed/{id}", token);
        Assert.Contains("There are no failed recipients to retry.", await client.FollowAsync(response));
    }

    [Fact]
    public async Task Cancel_WhenNotRunningShowsError()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv);
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));
        var response = await client.PostWithTokenAsync($"/Campaigns/Cancel/{id}", token);
        Assert.Contains("This campaign is not running.", await client.FollowAsync(response));
    }

    [Fact]
    public async Task Cancel_StopsARunningCampaign()
    {
        await using var server = new FakeSmtpServer { DataDelay = TimeSpan.FromMilliseconds(400) };
        var client = await TenantAsync();
        await client.SaveSmtpAsync(server.Port, concurrency: 1);
        var id = await client.CreateCampaignAsync(TestData.Csv(30));
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));
        await client.PostWithTokenAsync($"/Campaigns/Run/{id}", token);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.Messages.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50);

        var running = await client.GetOkAsync($"/Campaigns/Details/{id}");
        Assert.Contains("Stop campaign", running);
        var cancel = await client.PostWithTokenAsync($"/Campaigns/Cancel/{id}", ClientExtensions.ExtractToken(running));
        Assert.Contains("Cancellation requested.", await client.FollowAsync(cancel));

        var status = await client.WaitForCompletionAsync(id);
        Assert.Equal("Cancelled", status.GetProperty("status").GetString());
        Assert.True(status.GetProperty("pending").GetInt32() > 0);
        Assert.Equal("Cancelled by user.", status.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Run_AgainstUnreachableServerEndsFailedWithMessage()
    {
        var client = await TenantAsync();
        await client.SaveSmtpAsync(FakeSmtpServer.ClosedPort());
        var id = await client.CreateCampaignAsync(Csv);
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));
        await client.PostWithTokenAsync($"/Campaigns/Run/{id}", token);

        var status = await client.WaitForCompletionAsync(id);
        Assert.Equal("Failed", status.GetProperty("status").GetString());
        Assert.StartsWith("Could not connect to SMTP server", status.GetProperty("lastError").GetString());
        Assert.Equal(2, status.GetProperty("pending").GetInt32());
        Assert.Contains("Could not connect to SMTP server", await client.GetOkAsync($"/Campaigns/Details/{id}"));
    }

    [Fact]
    public async Task Delete_RemovesCampaignAndRecipients()
    {
        var client = await TenantAsync();
        var id = await client.CreateCampaignAsync(Csv, name: "Doomed");
        var token = ClientExtensions.ExtractToken(await client.GetOkAsync($"/Campaigns/Details/{id}"));

        var response = await client.PostWithTokenAsync($"/Campaigns/Delete/{id}", token);
        Assert.Equal("/Campaigns", response.Headers.Location?.ToString());
        var list = await client.FollowAsync(response);
        Assert.Contains("Campaign &quot;Doomed&quot; deleted.", list);
        Assert.DoesNotContain($"/Campaigns/Details/{id}", list);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Campaigns/Details/{id}")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_ShowsRecentCampaignsAndTotals()
    {
        var client = await TenantAsync();
        await client.CreateCampaignAsync(Csv, name: "Dash promo");
        var html = await client.GetOkAsync("/Dashboard");
        Assert.Contains("Dash promo", html);
        Assert.Contains("Recent campaigns", html);
    }
}
