using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Notify.Web.Constants;
using Notify.Web.Models;
using Notify.Web.Models.ViewModels;

namespace Notify.Web.Controllers;

public class AccountController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IWebHostEnvironment environment,
    ILogger<AccountController> logger) : Controller
{
    // ---------- Register ----------

    [HttpGet, AllowAnonymous]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        return View(new RegisterViewModel());
    }

    [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = new ApplicationUser
        {
            UserName = model.Email.Trim(),
            Email = model.Email.Trim(),
            CompanyName = model.CompanyName.Trim(),
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }

        logger.LogInformation("New tenant registered: {Email}", user.Email);
        await signInManager.SignInAsync(user, isPersistent: false);
        TempData["Success"] = "Welcome to Notify! Configure your SMTP server to start sending campaigns.";
        return RedirectToAction("Smtp", "Settings");
    }

    // ---------- Login / Logout ----------

    [HttpGet, AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await signInManager.PasswordSignInAsync(model.Email.Trim(), model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                return Redirect(model.ReturnUrl);
            return RedirectToAction("Index", "Dashboard");
        }

        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "This account is temporarily locked because of too many failed attempts. Try again later."
            : "Invalid email or password.");
        return View(model);
    }

    [HttpPost, Authorize, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    // ---------- Profile (company name + logo) ----------

    [HttpGet, Authorize]
    public async Task<IActionResult> Profile()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        return View(new ProfileViewModel
        {
            Email = user.Email ?? string.Empty,
            CompanyName = user.CompanyName,
            LogoPath = user.LogoPath
        });
    }

    [HttpPost, Authorize, ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(ProfileViewModel model)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        model.Email = user.Email ?? string.Empty;
        model.LogoPath = user.LogoPath;

        if (model.Logo is not null)
        {
            var ext = Path.GetExtension(model.Logo.FileName).ToLowerInvariant();
            if (!LogoUpload.AllowedExtensions.Contains(ext))
                ModelState.AddModelError(nameof(model.Logo), "Logo must be a PNG, JPG, GIF or WEBP image.");
            else if (model.Logo.Length > LogoUpload.MaxBytes)
                ModelState.AddModelError(nameof(model.Logo), "Logo must be smaller than 2 MB.");
        }

        if (!ModelState.IsValid) return View(model);

        user.CompanyName = model.CompanyName.Trim();

        if (model.RemoveLogo && !string.IsNullOrEmpty(user.LogoPath))
        {
            DeleteLogoFile(user.LogoPath);
            user.LogoPath = null;
        }

        if (model.Logo is not null)
        {
            var ext = Path.GetExtension(model.Logo.FileName).ToLowerInvariant();
            var folder = Path.Combine(environment.WebRootPath, LogoUpload.Folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(folder);
            var fileName = $"{user.Id}_{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(folder, fileName);

            await using (var stream = System.IO.File.Create(fullPath))
            {
                await model.Logo.CopyToAsync(stream);
            }

            if (!string.IsNullOrEmpty(user.LogoPath)) DeleteLogoFile(user.LogoPath);
            user.LogoPath = $"/{LogoUpload.Folder}/{fileName}";
        }

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }

        TempData["Success"] = "Profile updated.";
        return RedirectToAction(nameof(Profile));
    }

    // ---------- Change password ----------

    [HttpGet, Authorize]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost, Authorize, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var result = await userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }

        await signInManager.RefreshSignInAsync(user);
        TempData["Success"] = "Password changed.";
        return RedirectToAction(nameof(Profile));
    }

    [HttpGet, AllowAnonymous]
    public IActionResult AccessDenied() => View();

    private void DeleteLogoFile(string logoPath)
    {
        try
        {
            var relative = logoPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(environment.WebRootPath, relative);
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete old logo {Path}", logoPath);
        }
    }
}
