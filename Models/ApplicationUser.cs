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
}
