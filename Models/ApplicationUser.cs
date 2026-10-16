using Microsoft.AspNetCore.Identity;

namespace TimeManagement.Models;

/// <summary>
/// A TimeManage user account. Extends the Identity user with the profile
/// fields the scheduling features will need in later sprints.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>Name shown in the UI, collected at registration.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>When the account was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The colour palette the user picked on the settings page.
    /// Matches one of the Id values in <see cref="ColourPalettes"/>.
    /// </summary>
    public string ColourPalette { get; set; } = ColourPalettes.DefaultId;

    /// <summary>Lead times offered for session reminders, in minutes.</summary>
    public static readonly int[] ReminderLeadOptions = [10, 15, 30];

    /// <summary>Whether study-session reminders are also emailed (the
    /// in-app reminder always shows). Set on Profile &gt; Details.</summary>
    public bool EmailRemindersEnabled { get; set; } = true;

    /// <summary>How many minutes before a session its reminder appears
    /// (and its email is sent) - one of <see cref="ReminderLeadOptions"/>.</summary>
    public int ReminderLeadMinutes { get; set; } = 15;

    /// <summary>The Telegram chat linked on Profile &gt; Details, where
    /// reminders (with +15 min / Cancel buttons) are sent. Null = not connected.</summary>
    public long? TelegramChatId { get; set; }

    /// <summary>The linked chat's @username, just for showing who's connected.</summary>
    public string? TelegramUsername { get; set; }

    /// <summary>Whether reminders go to the linked Telegram chat - lets the
    /// student pause them without disconnecting.</summary>
    public bool TelegramRemindersEnabled { get; set; }

    /// <summary>One-time code in the "Connect Telegram" link (t.me/bot?start=code);
    /// the bot links whichever chat presses Start with it. Short-lived.</summary>
    public string? TelegramLinkCode { get; set; }

    public DateTime? TelegramLinkCodeExpiresUtc { get; set; }
}
