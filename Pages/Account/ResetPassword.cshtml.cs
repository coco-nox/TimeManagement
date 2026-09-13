using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using TimeManagement.Models;

namespace TimeManagement.Pages.Account;

/// <summary>
/// Where a password reset link (emailed by ForgotPassword.cshtml.cs) lands.
/// The userId and token in the link are round-tripped through hidden form
/// fields so they're still available on the POST that actually resets
/// the password.
/// </summary>
public class ResetPasswordModel(UserManager<ApplicationUser> userManager) : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>True once the password has actually been reset - the view
    /// swaps to a "success, go log in" message instead of the form.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Set when the link itself is the problem (missing userId/token,
    /// an unknown user, or a token Identity rejects as invalid/expired/tampered) -
    /// the form isn't shown at all in that case, only a "request a new one" prompt.
    /// Not set for an ordinary validation failure (e.g. a too-weak password),
    /// which keeps the form up with a normal field-level error instead.</summary>
    public bool IsLinkInvalid { get; set; }

    public class InputModel
    {
        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        public string Token { get; set; } = string.Empty;

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

    public IActionResult OnGet(string? userId, string? token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
        {
            IsLinkInvalid = true;
            return Page();
        }

        Input.UserId = userId;
        Input.Token = token;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.UserId) || string.IsNullOrWhiteSpace(Input.Token))
        {
            IsLinkInvalid = true;
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _userManager.FindByIdAsync(Input.UserId);
        if (user == null)
        {
            // A stale/tampered userId is indistinguishable from an expired
            // token as far as the student needs to know - same "link isn't
            // valid" outcome either way.
            IsLinkInvalid = true;
            return Page();
        }

        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Input.Token));
        }
        catch (FormatException)
        {
            IsLinkInvalid = true;
            return Page();
        }

        var result = await _userManager.ResetPasswordAsync(user, decodedToken, Input.NewPassword);
        if (!result.Succeeded)
        {
            // Identity reports a bad/expired/already-used token as the
            // specific error code "InvalidToken" - that's a dead link, not
            // something retyping the password fixes, so it gets the same
            // non-technical "request a new one" treatment as a malformed
            // link. Anything else (e.g. a password complexity rule the
            // DataAnnotations above don't already cover) is a normal,
            // fixable validation error - the token itself is still good, so
            // the form stays up rather than telling the student to start over.
            if (result.Errors.Any(e => e.Code == "InvalidToken"))
            {
                IsLinkInvalid = true;
                return Page();
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        IsComplete = true;
        return Page();
    }
}
