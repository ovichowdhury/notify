using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Notify.Web.Data;
using Notify.Web.Models;
using Notify.Web.Models.ViewModels;
using Notify.Web.Services;

namespace Notify.Web.Controllers;

[Authorize]
public class SettingsController(
    ApplicationDbContext db,
    SmtpPasswordProtector passwordProtector,
    SmtpClientFactory smtpClientFactory,
    ILogger<SettingsController> logger) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> Smtp()
    {
        var setting = await db.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == UserId);
        var model = setting is null
            ? new SmtpSettingsViewModel { FromEmail = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty }
            : new SmtpSettingsViewModel
            {
                Host = setting.Host,
                Port = setting.Port,
                Security = setting.Security,
                Username = setting.Username,
                HasSavedPassword = !string.IsNullOrEmpty(setting.PasswordEncrypted),
                FromEmail = setting.FromEmail,
                FromName = setting.FromName,
                ConcurrencyLevel = setting.ConcurrencyLevel,
                DelayBetweenEmailsMs = setting.DelayBetweenEmailsMs,
                UpdatedAt = setting.UpdatedAt
            };

        ViewBag.TestModel = new TestSmtpViewModel { ToEmail = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty };
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Smtp(SmtpSettingsViewModel model)
    {
        var setting = await db.SmtpSettings.FirstOrDefaultAsync(s => s.UserId == UserId);
        model.HasSavedPassword = !string.IsNullOrEmpty(setting?.PasswordEncrypted);

        if (!ModelState.IsValid)
        {
            ViewBag.TestModel = new TestSmtpViewModel { ToEmail = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty };
            return View(model);
        }

        if (setting is null)
        {
            setting = new SmtpSetting { UserId = UserId };
            db.SmtpSettings.Add(setting);
        }

        setting.Host = model.Host.Trim();
        setting.Port = model.Port;
        setting.Security = model.Security;
        setting.Username = string.IsNullOrWhiteSpace(model.Username) ? null : model.Username.Trim();
        setting.FromEmail = model.FromEmail.Trim();
        setting.FromName = (model.FromName ?? string.Empty).Trim();
        setting.ConcurrencyLevel = Math.Clamp(model.ConcurrencyLevel, 1, CampaignRunner.MaxConcurrency);
        setting.DelayBetweenEmailsMs = Math.Clamp(model.DelayBetweenEmailsMs, 0, 60_000);
        setting.UpdatedAt = DateTime.UtcNow;

        // A blank password keeps the previously saved one; clearing the username clears the password.
        if (!string.IsNullOrEmpty(model.Password))
            setting.PasswordEncrypted = passwordProtector.Protect(model.Password);
        else if (setting.Username is null)
            setting.PasswordEncrypted = null;

        await db.SaveChangesAsync();
        TempData["Success"] = "SMTP settings saved.";
        return RedirectToAction(nameof(Smtp));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestSmtp(TestSmtpViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Enter a valid email address for the test message.";
            return RedirectToAction(nameof(Smtp));
        }

        var setting = await db.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == UserId);
        if (setting is null)
        {
            TempData["Error"] = "Save your SMTP settings before sending a test email.";
            return RedirectToAction(nameof(Smtp));
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var client = await smtpClientFactory.CreateConnectedAsync(setting, passwordProtector.Unprotect(setting.PasswordEncrypted), cts.Token);
            var message = SmtpClientFactory.BuildMessage(
                setting,
                model.ToEmail.Trim(),
                null,
                "Notify SMTP test",
                "<p>Your SMTP configuration in <strong>Notify</strong> works. This is a test message.</p>");
            await client.SendAsync(message, cts.Token);
            await client.DisconnectAsync(true, cts.Token);
            TempData["Success"] = $"Test email sent to {model.ToEmail}.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP test failed for user {UserId}", UserId);
            TempData["Error"] = "Test email failed: " + ex.Message;
        }

        return RedirectToAction(nameof(Smtp));
    }
}
