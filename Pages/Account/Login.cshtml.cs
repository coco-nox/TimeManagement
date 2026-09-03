using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeManagement.Models;

namespace TimeManagement.Pages.Account;

public class LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, ILogger<LoginModel> logger) : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager = signInManager;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly ILogger<LoginModel> _logger = logger;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your email address.")]
        [EmailAddress(ErrorMessage = "That doesn't look like a valid email address.")]
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Keep me signed in")]
        public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        // Clear any stale external cookie so a failed sign-in starts clean.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // lockoutOnFailure: true counts this attempt towards the lockout
        // threshold configured in Program.cs.
        var result = await _signInManager.PasswordSignInAsync(
            Input.Email,
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: true);

        if (result.Succeeded)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("{Email} signed in.", Input.Email);
            }
            return LocalRedirect(returnUrl ?? Url.Content("~/"));
        }

        if (result.IsLockedOut)
        {
            _logger.LogWarning("{Email} is locked out.", Input.Email);
            ModelState.AddModelError(string.Empty,
                "This account is temporarily locked after too many failed attempts. Try again in a few minutes, " +
                "or use \"Forgot your password?\" below if you don't remember it.");
            return Page();
        }

        // Only show a remaining-attempts count for a real account - counting
        // down attempts for an email that doesn't exist would reveal whether
        // it's registered, which is exactly what the vague message below is
        // there to avoid.
        var user = await _userManager.FindByEmailAsync(Input.Email);
        if (user != null)
        {
            var attemptsUsed = await _userManager.GetAccessFailedCountAsync(user);
            var attemptsLeft = _userManager.Options.Lockout.MaxFailedAccessAttempts - attemptsUsed;
            var attemptWord = attemptsLeft == 1 ? "attempt" : "attempts";
            ModelState.AddModelError(string.Empty,
                $"Incorrect password. You have {attemptsLeft} {attemptWord} left before this account is temporarily locked.");
            return Page();
        }

        // Deliberately vague: don't reveal whether the email exists.
        ModelState.AddModelError(string.Empty, "Incorrect email or password.");
        return Page();
    }
}
