using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeManagement.Models;

namespace TimeManagement.Pages;

public class ChangePasswordModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public ChangePasswordModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Shown once after a successful save.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your current password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please choose a new password.")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Your password must be at least 8 characters.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The two passwords don't match.")]
        public string ConfirmNewPassword { get; set; } = string.Empty;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        // ChangePasswordAsync checks the current password itself, so we
        // don't need to verify it separately before calling this.
        var result = await _userManager.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // Changing the password rotates the user's security stamp, which
        // would otherwise sign them out immediately. Refresh the cookie so
        // they stay logged in after saving.
        await _signInManager.RefreshSignInAsync(user);

        StatusMessage = "Your password has been changed.";

        // Redirect rather than returning Page() so refreshing the browser
        // doesn't re-submit the form.
        return RedirectToPage();
    }
}
