using MailKit.Security;
using Notify.Test.TestSupport;
using Notify.Web.Models;
using Notify.Web.Services;

namespace Notify.Test.Unit;

public class SmtpClientFactoryTests
{
    private readonly SmtpClientFactory _factory = new();

    [Fact]
    public void BuildMessage_SetsHeadersAndBothBodies()
    {
        var settings = TestData.Smtp("u", 25);
        settings.FromName = "Acme Marketing";
        settings.FromEmail = "news@acme.test";

        var message = SmtpClientFactory.BuildMessage(settings, "alice@example.com", "Alice", "Hello Alice", "<p>Hi <b>Alice</b> &amp; team</p>");

        Assert.Equal("Acme Marketing", message.From.Mailboxes.Single().Name);
        Assert.Equal("news@acme.test", message.From.Mailboxes.Single().Address);
        Assert.Equal("Alice", message.To.Mailboxes.Single().Name);
        Assert.Equal("alice@example.com", message.To.Mailboxes.Single().Address);
        Assert.Equal("Hello Alice", message.Subject);
        Assert.Equal("<p>Hi <b>Alice</b> &amp; team</p>", message.HtmlBody);
        Assert.Equal("Hi Alice & team", message.TextBody);
    }

    [Fact]
    public void BuildMessage_ToleratesNullNames()
    {
        var settings = TestData.Smtp("u", 25);
        settings.FromName = null!;
        var message = SmtpClientFactory.BuildMessage(settings, "a@example.com", null, "s", "<p>b</p>");
        Assert.Equal(string.Empty, message.To.Mailboxes.Single().Name);
        Assert.Equal(string.Empty, message.From.Mailboxes.Single().Name);
    }

    [Fact]
    public async Task CreateConnected_ConnectsWithoutAuthWhenNoUsername()
    {
        await using var server = new FakeSmtpServer();
        using var client = await _factory.CreateConnectedAsync(TestData.Smtp("u", server.Port), null, CancellationToken.None);

        Assert.True(client.IsConnected);
        Assert.False(client.IsAuthenticated);
        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task CreateConnected_AuthenticatesWithCredentials()
    {
        await using var server = new FakeSmtpServer { RequiredCredentials = ("mailer", "pw") };
        using var client = await _factory.CreateConnectedAsync(TestData.Smtp("u", server.Port, username: "mailer"), "pw", CancellationToken.None);

        Assert.True(client.IsAuthenticated);
        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task CreateConnected_WrongPasswordThrowsAuthenticationException()
    {
        await using var server = new FakeSmtpServer { RequiredCredentials = ("mailer", "pw") };
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            _factory.CreateConnectedAsync(TestData.Smtp("u", server.Port, username: "mailer"), "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task CreateConnected_ClosedPortThrows()
    {
        var settings = TestData.Smtp("u", FakeSmtpServer.ClosedPort());
        await Assert.ThrowsAnyAsync<Exception>(() => _factory.CreateConnectedAsync(settings, null, CancellationToken.None));
    }

    [Fact]
    public async Task CreateConnected_HonoursCancellation()
    {
        var settings = TestData.Smtp("u", FakeSmtpServer.ClosedPort());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _factory.CreateConnectedAsync(settings, null, cts.Token));
    }

    [Fact]
    public async Task CreateConnected_SendsAMessageEndToEnd()
    {
        await using var server = new FakeSmtpServer();
        var settings = TestData.Smtp("u", server.Port);
        using var client = await _factory.CreateConnectedAsync(settings, null, CancellationToken.None);

        await client.SendAsync(SmtpClientFactory.BuildMessage(settings, "bob@example.com", "Bob", "Subject line", "<p>Body</p>"));
        await client.DisconnectAsync(true);

        var message = Assert.Single(server.Messages);
        Assert.Equal("noreply@test.local", message.From);
        Assert.Equal("bob@example.com", Assert.Single(message.Recipients));
        Assert.Contains("Subject: Subject line", message.Data);
        Assert.Contains("<p>Body</p>", message.Data);
    }
}
