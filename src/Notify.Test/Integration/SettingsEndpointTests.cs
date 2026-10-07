using System.Net;
using Notify.Test.TestSupport;

namespace Notify.Test.Integration;

public class SettingsEndpointTests(NotifyWebFactory factory) : IClassFixture<NotifyWebFactory>
{
    [Fact]
    public async Task Get_PrefillsFromEmailWithLoginEmailForNewTenant()
    {
        var client = factory.CreateBrowser();
        var email = await client.RegisterAsync();
        var html = await client.GetOkAsync("/Settings/Smtp");
        Assert.Contains($"value=\"{email}\"", html);
        Assert.Contains("Stored encrypted at rest.", html);
        Assert.Contains("Send test", html);
        Assert.Contains("disabled", html); // test button disabled until settings exist
    }

    [Fact]
    public async Task Post_InvalidModelShowsValidationErrors()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        var response = await client.PostFormAsync("/Settings/Smtp", "/Settings/Smtp",
            ("Host", ""), ("Port", "70000"), ("Security", "2"), ("FromEmail", "not-an-email"), ("ConcurrencyLevel", "99"), ("DelayBetweenEmailsMs", "0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("field-validation-error", html);
        Assert.Contains("The field Port must be between 1 and 65535.", html);
        Assert.Contains("must be between 1 and 50", html);
    }

    [Fact]
    public async Task Post_SavesSettingsAndKeepsPasswordWhenBlank()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();

        await client.SaveSmtpAsync(2525, concurrency: 7, host: "smtp.example.com", username: "mailer", password: "pw");
        var saved = await client.GetOkAsync("/Settings/Smtp");
        Assert.Contains("SMTP settings saved.", saved);
        Assert.Contains("value=\"smtp.example.com\"", saved);
        Assert.Contains("value=\"7\"", saved);
        Assert.Contains("A password is saved. Leave blank to keep it.", saved);
        Assert.DoesNotContain("value=\"pw\"", saved); // password never echoed

        // Re-save with blank password: still saved.
        await client.SaveSmtpAsync(2525, concurrency: 7, host: "smtp.example.com", username: "mailer");
        Assert.Contains("A password is saved.", await client.GetOkAsync("/Settings/Smtp"));

        // Clear the username: password is dropped.
        await client.SaveSmtpAsync(2525, concurrency: 7, host: "smtp.example.com");
        var cleared = await client.GetOkAsync("/Settings/Smtp");
        Assert.DoesNotContain("A password is saved.", cleared);
        Assert.Contains("Stored encrypted at rest.", cleared);
    }

    [Fact]
    public async Task Post_ClampsConcurrencyAndDelay()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        await client.SaveSmtpAsync(2525, concurrency: 50, delayMs: 60000);
        var html = await client.GetOkAsync("/Settings/Smtp");
        Assert.Contains("value=\"50\"", html);
        Assert.Contains("value=\"60000\"", html);
    }

    [Fact]
    public async Task TestSmtp_WithoutSavedSettingsShowsHint()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        var response = await client.PostFormAsync("/Settings/TestSmtp", "/Settings/Smtp", ("ToEmail", "me@example.com"));
        var page = await client.FollowAsync(response);
        Assert.Contains("Save your SMTP settings before sending a test email.", page);
    }

    [Fact]
    public async Task TestSmtp_InvalidAddressShowsError()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        var response = await client.PostFormAsync("/Settings/TestSmtp", "/Settings/Smtp", ("ToEmail", "nope"));
        Assert.Contains("Enter a valid email address", await client.FollowAsync(response));
    }

    [Fact]
    public async Task TestSmtp_SendsThroughConfiguredServer()
    {
        await using var server = new FakeSmtpServer();
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        await client.SaveSmtpAsync(server.Port);

        var response = await client.PostFormAsync("/Settings/TestSmtp", "/Settings/Smtp", ("ToEmail", "inbox@example.com"));
        var page = await client.FollowAsync(response);

        Assert.Contains("Test email sent to inbox@example.com.", page);
        var message = Assert.Single(server.Messages);
        Assert.Equal("inbox@example.com", Assert.Single(message.Recipients));
        Assert.Contains("Subject: Notify SMTP test", message.Data);
    }

    [Fact]
    public async Task TestSmtp_ReportsConnectionFailure()
    {
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        await client.SaveSmtpAsync(FakeSmtpServer.ClosedPort());

        var response = await client.PostFormAsync("/Settings/TestSmtp", "/Settings/Smtp", ("ToEmail", "inbox@example.com"));
        Assert.Contains("Test email failed:", await client.FollowAsync(response));
    }

    [Fact]
    public async Task TestSmtp_ReportsAuthenticationFailure()
    {
        await using var server = new FakeSmtpServer { RequiredCredentials = ("mailer", "right") };
        var client = factory.CreateBrowser();
        await client.RegisterAsync();
        await client.SaveSmtpAsync(server.Port, username: "mailer", password: "wrong");

        var response = await client.PostFormAsync("/Settings/TestSmtp", "/Settings/Smtp", ("ToEmail", "inbox@example.com"));
        var page = await client.FollowAsync(response);
        Assert.Contains("Test email failed:", page);
        Assert.Contains("Authentication failed", page);
    }

    [Fact]
    public async Task Settings_AreIsolatedPerTenant()
    {
        var a = factory.CreateBrowser();
        await a.RegisterAsync();
        await a.SaveSmtpAsync(1111, host: "tenant-a.example.com");

        var b = factory.CreateBrowser();
        await b.RegisterAsync();
        var html = await b.GetOkAsync("/Settings/Smtp");
        Assert.DoesNotContain("tenant-a.example.com", html);
    }
}
