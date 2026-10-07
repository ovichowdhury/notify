using System.ComponentModel.DataAnnotations;

namespace Notify.Web.Models.ViewModels;

public class RegisterViewModel
{
    [Required, MaxLength(200)]
    [Display(Name = "Company name")]
    public string CompanyName { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "The password must be at least 6 characters long.")]
    public string Password { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class LoginViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class ProfileViewModel
{
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    [Display(Name = "Company name")]
    public string CompanyName { get; set; } = string.Empty;

    public string? LogoPath { get; set; }

    [Display(Name = "Company logo")]
    public IFormFile? Logo { get; set; }

    public bool RemoveLogo { get; set; }
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "The password must be at least 6 characters long.")]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    [Display(Name = "Confirm new password")]
    [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
