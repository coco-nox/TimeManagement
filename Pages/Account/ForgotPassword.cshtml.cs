using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Account;

public class ForgotPasswordModel(UserManager<ApplicationUser> userManager, EmailSender emailSender) : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly EmailSender _emailSender = emailSender;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>True once the form has been submitted - the view swaps to
    /// the "check your email" message instead of the form. Set unconditionally
    /// on a successful post, whether or not the email address was actually
    /// registered - see OnPostAsync.</summary>
    public bool EmailSent { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your email address.")]
        [EmailAddress(ErrorMessage = "That doesn't look like a valid email address.")]
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;
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

        var user = await _userManager.FindByEmailAsync(Input.Email);

        // Deliberately identical outcome whether or not the account exists -
        // revealing that via a different message (or response time - the
        // email send below is awaited either way) would let someone
        // enumerate which emails are registered.
        if (user != null)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);

            // Identity's raw token can contain characters (+, /, =) that
            // aren't safe to embed directly in a URL query string; encoding
            // it here (and decoding in ResetPassword.cshtml.cs) is the same
            // pattern ASP.NET Core Identity's own scaffolded UI uses.
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

            var resetLink = Url.Page(
                "/Account/ResetPassword",
                pageHandler: null,
                values: new { userId = user.Id, token = encodedToken },
                protocol: Request.Scheme)!;

            await _emailSender.SendPasswordResetEmailAsync(user.Email!, resetLink);
        }

        EmailSent = true;
        return Page();
    }
}
