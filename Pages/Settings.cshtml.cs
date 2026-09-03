using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeManagement.Models;

namespace TimeManagement.Pages;

public class SettingsModel(UserManager<ApplicationUser> userManager) : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;

    /// <summary>The seven palettes shown as options on the page.</summary>
    public List<ColourPalette> Palettes { get; } = ColourPalettes.All;

    /// <summary>The id of the palette the user currently has selected.</summary>
    [BindProperty]
    public string SelectedPalette { get; set; } = ColourPalettes.DefaultId;

    /// <summary>Shown once after a successful save.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        SelectedPalette = user.ColourPalette;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        // Never trust what comes back from the form: if it isn't one of our
        // seven ids, fall back to the default rather than saving it.
        if (!ColourPalettes.IsValid(SelectedPalette))
        {
            SelectedPalette = ColourPalettes.DefaultId;
        }

        user.ColourPalette = SelectedPalette;
        await _userManager.UpdateAsync(user);

        StatusMessage = "Your colour palette has been saved.";

        // Redirect rather than returning Page() so refreshing the browser
        // doesn't re-submit the form.
        return RedirectToPage();
    }
}
