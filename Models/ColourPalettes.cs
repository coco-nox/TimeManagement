namespace TimeManagement.Models;

/// <summary>
/// One selectable colour palette. The <see cref="Id"/> is what gets saved
/// against the user and written into the page as data-palette="...";
/// the matching colours live in wwwroot/css/palettes.css.
/// </summary>
public class ColourPalette
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // Three colours shown as a little preview strip on the settings page.
    public string Swatch1 { get; set; } = "";
    public string Swatch2 { get; set; } = "";
    public string Swatch3 { get; set; } = "";
}

/// <summary>
/// The list of palettes the user can choose from. To add an eighth palette,
/// add an entry here and a matching block in wwwroot/css/palettes.css.
/// </summary>
public static class ColourPalettes
{
    public const string DefaultId = "default";

    public static readonly List<ColourPalette> All = new()
    {
        new ColourPalette
        {
            Id = "default",
            Name = "TimeManage",
            Description = "Warm sage and terracotta, calm and earthy.",
            Swatch1 = "#F2E8D9", Swatch2 = "#DFA477", Swatch3 = "#6B7F6B"
        },
        new ColourPalette
        {
            Id = "ocean",
            Name = "Ocean",
            Description = "Cool teals and blues.",
            Swatch1 = "#f2fafb", Swatch2 = "#cfe9ee", Swatch3 = "#0f766e"
        },
        new ColourPalette
        {
            Id = "forest",
            Name = "Forest",
            Description = "Calm greens.",
            Swatch1 = "#f4f9f4", Swatch2 = "#d7e8d7", Swatch3 = "#2f6b3f"
        },
        new ColourPalette
        {
            Id = "sunset",
            Name = "Sunset",
            Description = "Warm oranges and pinks.",
            Swatch1 = "#fff7f2", Swatch2 = "#fadfd0", Swatch3 = "#c2410c"
        },
        new ColourPalette
        {
            Id = "lavender",
            Name = "Lavender",
            Description = "Soft purples.",
            Swatch1 = "#faf7fd", Swatch2 = "#e4daf2", Swatch3 = "#6d28d9"
        },
        new ColourPalette
        {
            Id = "midnight",
            Name = "Midnight",
            Description = "Dark theme, easier on the eyes at night.",
            Swatch1 = "#161a22", Swatch2 = "#232a36", Swatch3 = "#60a5fa"
        },
        new ColourPalette
        {
            Id = "contrast",
            Name = "High contrast",
            Description = "Maximum contrast and larger focus outlines.",
            Swatch1 = "#ffffff", Swatch2 = "#e0e0e0", Swatch3 = "#000000"
        }
    };

    /// <summary>
    /// True if the given id is one of our palettes. Used to reject anything
    /// unexpected that gets posted to the settings page.
    /// </summary>
    public static bool IsValid(string? id)
    {
        foreach (var palette in All)
        {
            if (palette.Id == id)
            {
                return true;
            }
        }

        return false;
    }
}
