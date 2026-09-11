namespace TimeManagement.Models;

/// <summary>
/// A course a user is studying (e.g. "ITEC631"). Each course owns one or
/// more assessments, and each assessment owns the documents uploaded for it.
/// </summary>
public class Course
{
    public int Id { get; set; }

    /// <summary>The owning user. Every query must filter on this so a user
    /// can never see another user's courses.</summary>
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The colour of this course's "folder" card on the Dashboard,
    /// as a hex string. Always one of <see cref="FolderColours.All"/> -
    /// never freeform, so a bad value in the database can't break the picker.</summary>
    public string ColourHex { get; set; } = FolderColours.DefaultHex;

    public List<Assessment> Assessments { get; set; } = [];
}

/// <summary>
/// The fixed set of "standard" colours a student can pick for a course's
/// Dashboard folder card - plain, generic colours rather than anything tied
/// to the app's 7 branded colour palettes (see Models/ColourPalettes.cs),
/// since a course's colour is the student's own labelling, not a theme choice.
/// </summary>
public static class FolderColours
{
    public const string DefaultHex = "#3b82f6"; // Blue

    public static readonly List<(string Hex, string Name)> All =
    [
        ("#ef4444", "Red"),
        ("#f97316", "Orange"),
        ("#f59e0b", "Amber"),
        ("#eab308", "Yellow"),
        ("#22c55e", "Green"),
        ("#14b8a6", "Teal"),
        ("#3b82f6", "Blue"),
        ("#6366f1", "Indigo"),
        ("#a855f7", "Purple"),
        ("#ec4899", "Pink"),
        ("#6b7280", "Gray")
    ];

    /// <summary>True if the given hex is one of the standard colours. Used to
    /// reject anything unexpected posted to the "change folder colour" handler.</summary>
    public static bool IsValid(string? hex)
    {
        return All.Any(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase));
    }
}
