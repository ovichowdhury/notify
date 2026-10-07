using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Notify.Web.Models;

namespace Notify.Web.Services;

public class SmtpClientFactory
{
    public async Task<SmtpClient> CreateConnectedAsync(SmtpSetting settings, string? password, CancellationToken ct)
    {
        var client = new SmtpClient { Timeout = 30_000 };
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, Map(settings.Security), ct);
            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, password ?? string.Empty, ct);
            }
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static MimeMessage BuildMessage(SmtpSetting settings, string toEmail, string? toName, string subject, string html)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? string.Empty, settings.FromEmail));
        message.To.Add(new MailboxAddress(toName ?? string.Empty, toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder
        {
            HtmlBody = html,
            TextBody = TemplateRenderer.ToPlainText(html)
        }.ToMessageBody();
        return message;
    }

    private static SecureSocketOptions Map(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto
    };
}
