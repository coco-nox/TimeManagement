using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages;

/// <summary>
/// The sidebar's Profile &gt; Details section: shows and updates the
/// account's full name and email address. Password changes stay on the
/// existing ChangePassword page rather than being duplicated here.
/// </summary>
public class AccountDetailsModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    EmailSender emailSender,
    ITelegramClient telegram,
    IConfiguration configuration) : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly SignInManager<ApplicationUser> _signInManager = signInManager;
    private readonly EmailSender _emailSender = emailSender;
    private readonly ITelegramClient _telegram = telegram;
    private readonly IConfiguration _configuration = configuration;

    private static readonly TimeSpan TelegramLinkLifetime = TimeSpan.FromMinutes(15);

    // Telegram card state.
    public bool TelegramConfigured { get; set; }

    /// <summary>The bot's @username; null when the token is set but wrong.</summary>
    public string? TelegramBotUsername { get; set; }

    public bool TelegramConnected { get; set; }

    public string? TelegramUsername { get; set; }

    public bool TelegramRemindersEnabled { get; set; }

    /// <summary>The t.me link with a live one-time code, after Connect was clicked.</summary>
    public string? TelegramConnectLink { get; set; }

    public string? TelegramConnectCode { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Shown once after a successful save.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    // Session reminder settings, shown in their own card.
    public bool EmailRemindersEnabled { get; set; }

    public int ReminderLeadMinutes { get; set; }

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
        LoadReminderSettings(user);
        await LoadTelegramAsync(user);

        return Page();
    }

    /// <summary>Makes a one-time code and shows the t.me link that carries
    /// it; the bot links whichever chat presses Start with it.</summary>
    public async Task<IActionResult> OnPostConnectTelegramAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        if (await _telegram.GetBotUsernameAsync() == null)
        {
            ErrorMessage = "FlightPath can't reach your Telegram bot - check the bot token in User Secrets.";
            return RedirectToPage(null, null, null, "telegram");
        }

        user.TelegramLinkCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        user.TelegramLinkCodeExpiresUtc = DateTime.UtcNow.Add(TelegramLinkLifetime);
        await _userManager.UpdateAsync(user);
        return RedirectToPage(null, null, null, "telegram");
    }

    public async Task<IActionResult> OnPostUpdateTelegramAsync([FromForm] bool telegramReminders)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        user.TelegramRemindersEnabled = telegramReminders && user.TelegramChatId != null;
        await _userManager.UpdateAsync(user);
        StatusMessage = user.TelegramRemindersEnabled ? "Telegram reminders are on." : "Telegram reminders are paused.";
        return RedirectToPage(null, null, null, "telegram");
    }

    public async Task<IActionResult> OnPostDisconnectTelegramAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        if (user.TelegramChatId is long chatId)
        {
            await _telegram.SendMessageAsync(chatId, "Disconnected from FlightPath. You won't get reminders here any more.", null);
        }

        user.TelegramChatId = null;
        user.TelegramUsername = null;
        user.TelegramRemindersEnabled = false;
        user.TelegramLinkCode = null;
        user.TelegramLinkCodeExpiresUtc = null;
        await _userManager.UpdateAsync(user);
        StatusMessage = "Telegram disconnected.";
        return RedirectToPage(null, null, null, "telegram");
    }

    public async Task<IActionResult> OnPostSendTestTelegramAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user?.TelegramChatId is not long chatId)
        {
            ErrorMessage = "Connect Telegram first.";
            return RedirectToPage(null, null, null, "telegram");
        }

        var sent = await _telegram.SendMessageAsync(chatId,
            $"🔔 <b>Test reminder</b>\n\nThis is how FlightPath reminders arrive - {user.ReminderLeadMinutes} minutes before each " +
            "study session, with <b>+15 min</b>, <b>+30 min</b>, <b>Cancel session</b> and <b>Got it</b> buttons.", null);

        if (sent != null)
        {
            StatusMessage = "Test message sent - check Telegram on your phone.";
        }
        else
        {
            ErrorMessage = "The test message couldn't be sent. Check the bot token, and that you haven't blocked the bot in Telegram.";
        }
        return RedirectToPage(null, null, null, "telegram");
    }

    private async Task LoadTelegramAsync(ApplicationUser user)
    {
        TelegramConfigured = _telegram.IsConfigured;
        TelegramBotUsername = TelegramConfigured ? await _telegram.GetBotUsernameAsync() : null;
        TelegramConnected = user.TelegramChatId != null;
        TelegramUsername = user.TelegramUsername;
        TelegramRemindersEnabled = user.TelegramRemindersEnabled;

        if (!TelegramConnected && TelegramBotUsername != null &&
            user.TelegramLinkCode != null && user.TelegramLinkCodeExpiresUtc > DateTime.UtcNow)
        {
            TelegramConnectCode = user.TelegramLinkCode;
            TelegramConnectLink = $"https://t.me/{TelegramBotUsername}?start={user.TelegramLinkCode}";
        }
    }

    public async Task<IActionResult> OnPostUpdateRemindersAsync([FromForm] bool emailReminders, [FromForm] int leadMinutes)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        if (!ApplicationUser.ReminderLeadOptions.Contains(leadMinutes))
        {
            ErrorMessage = "Pick how early you'd like reminders.";
            return RedirectToPage(null, null, null, "reminders");
        }

        user.EmailRemindersEnabled = emailReminders;
        user.ReminderLeadMinutes = leadMinutes;
        await _userManager.UpdateAsync(user);

        StatusMessage = "Your reminder settings have been saved.";
        return RedirectToPage(null, null, null, "reminders");
    }

    /// <summary>Sends a sample reminder email right now, so the student can
    /// check it arrives (and notifies on their phone) without waiting for a
    /// real session.</summary>
    public async Task<IActionResult> OnPostSendTestReminderAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
        {
            return NotFound("Could not load your account.");
        }

        var start = DateTime.Now.AddMinutes(user.ReminderLeadMinutes);
        var sample = new SessionReminderEmail(
            "Test reminder - your sessions will look like this",
            null,
            FolderColours.DefaultHex,
            $"Today, {start:h:mm} – {start.AddMinutes(60):h:mm tt} · 60 minutes",
            null,
            user.ReminderLeadMinutes,
            _configuration["Reminders:AppBaseUrl"] ?? "http://localhost:5243");

        if (await _emailSender.SendSessionReminderAsync(user.Email, sample))
        {
            StatusMessage = $"Test reminder sent to {user.Email} - check your inbox (and your phone).";
        }
        else
        {
            ErrorMessage = "The test reminder couldn't be sent. Check that the Gmail address and app password are set up in User Secrets.";
        }

        return RedirectToPage(null, null, null, "reminders");
    }

    private void LoadReminderSettings(ApplicationUser user)
    {
        EmailRemindersEnabled = user.EmailRemindersEnabled;
        ReminderLeadMinutes = user.ReminderLeadMinutes;
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
        if (user != null)
        {
            LoadReminderSettings(user);
            await LoadTelegramAsync(user);
        }
        return Page();
    }

    private async Task<IActionResult> ReloadWithCurrentFullNameAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        Input.FullName = user?.FullName ?? string.Empty;
        if (user != null)
        {
            LoadReminderSettings(user);
            await LoadTelegramAsync(user);
        }
        return Page();
    }
}
