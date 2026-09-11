using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeManagement.Models;

namespace TimeManagement.Pages;

/// <summary>
/// The sidebar's Profile &gt; Details section: shows and updates the
/// account's full name and email address. Password changes stay on the
/// existing ChangePassword page rather than being duplicated here.
/// </summary>
public class AccountDetailsModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly SignInManager<ApplicationUser> _signInManager = signInManager;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Shown once after a successful save.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your name.")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Your name must be between 2 and 100 characters.")]
        [Display(Name = "Full name")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your email address.")]
        [EmailAddress(ErrorMessage = "That doesn't look like a valid email address.")]
        [StringLength(256)]
        [Display(Name = "Email address")]
        public string Email { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        Input.FullName = user.FullName;
        Input.Email = user.Email ?? string.Empty;

        return Page();
    }

    public async Task<IActionResult> OnPostUpdateFullNameAsync()
    {
        // Model binding auto-validates every Input property, including
        // Email, even though this form doesn't post it - clear its spurious
        // "required" error before deciding this form's own validity.
        ModelState.ClearValidationState($"{nameof(Input)}.{nameof(Input.Email)}");

        if (!TryValidateFullNameOnly())
        {
            return await ReloadWithCurrentEmailAsync();
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        user.FullName = Input.FullName.Trim();
        await _userManager.UpdateAsync(user);

        StatusMessage = "Your name has been updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateEmailAsync()
    {
        ModelState.ClearValidationState($"{nameof(Input)}.{nameof(Input.FullName)}");

        if (!TryValidateEmailOnly())
        {
            return await ReloadWithCurrentFullNameAsync();
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        var newEmail = Input.Email.Trim();

        // This app signs users in by email (see Account/Login.cshtml.cs,
        // which calls PasswordSignInAsync with the email as the username),
        // and Register.cshtml.cs sets UserName = Email at signup. UserName
        // and Email must stay in sync here too, or the user would be
        // locked out the next time they try to sign in.
        var emailResult = await _userManager.SetEmailAsync(user, newEmail);
        if (!emailResult.Succeeded)
        {
            foreach (var error in emailResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return await ReloadWithCurrentFullNameAsync();
        }

        var userNameResult = await _userManager.SetUserNameAsync(user, newEmail);
        if (!userNameResult.Succeeded)
        {
            foreach (var error in userNameResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return await ReloadWithCurrentFullNameAsync();
        }

        // Changing the email/username rotates the security stamp, which
        // would otherwise sign the user out immediately - same reason
        // ChangePassword.cshtml.cs refreshes the sign-in after a password change.
        await _signInManager.RefreshSignInAsync(user);

        StatusMessage = "Your email address has been updated.";
        return RedirectToPage();
    }

    private bool TryValidateFullNameOnly()
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(Input) { MemberName = nameof(Input.FullName) };
        var isValid = Validator.TryValidateProperty(Input.FullName, context, results);

        foreach (var result in results)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.FullName)}", result.ErrorMessage ?? "Invalid value.");
        }

        return isValid;
    }

    private bool TryValidateEmailOnly()
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(Input) { MemberName = nameof(Input.Email) };
        var isValid = Validator.TryValidateProperty(Input.Email, context, results);

        foreach (var result in results)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Email)}", result.ErrorMessage ?? "Invalid value.");
        }

        return isValid;
    }

    // The full-name form doesn't post the email field, so it needs
    // re-populating from the database before the page can be re-rendered
    // with a validation error - same for the email form and full name below.
    private async Task<IActionResult> ReloadWithCurrentEmailAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        Input.Email = user?.Email ?? string.Empty;
        return Page();
    }

    private async Task<IActionResult> ReloadWithCurrentFullNameAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        Input.FullName = user?.FullName ?? string.Empty;
        return Page();
    }
}
