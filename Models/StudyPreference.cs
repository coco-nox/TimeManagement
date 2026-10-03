namespace TimeManagement.Models;

/// <summary>
/// A student's top-level weekly study settings, set on the Calendar page's
/// Preferences tab. One row per user - see ApplicationDbContext's unique
/// index on UserId.
/// </summary>
public class StudyPreference
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public int TotalWeeklyHours { get; set; } = 10;

    /// <summary>Comma-separated day abbreviations the student is willing to
    /// study on, always stored in Mon-Sun order (e.g. "Mon,Tue,Wed,Thu,Fri") -
    /// see StudyDayNames.Normalize. Stored as a flat string rather than a
    /// child table since it's a small, fixed-shape set replaced as a whole
    /// every time the Preferences tab is saved.</summary>
    public string StudyDays { get; set; } = "Mon,Tue,Wed,Thu,Fri";
}

/// <summary>The fixed set of day abbreviations StudyPreference.StudyDays is
/// built from, in canonical Mon-Sun order.</summary>
public static class StudyDayNames
{
    public static readonly string[] All = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>Filters a raw list down to valid, deduplicated day names and
    /// orders them Mon-Sun regardless of the order they were submitted in,
    /// so a tampered or reordered form post can't produce a nonsense value.</summary>
    public static string Normalize(IEnumerable<string>? days)
    {
        if (days == null)
        {
            return string.Empty;
        }

        var selected = new HashSet<string>(days, StringComparer.OrdinalIgnoreCase);
        return string.Join(',', All.Where(selected.Contains));
    }

    public static List<string> Parse(string? studyDays)
    {
        if (string.IsNullOrWhiteSpace(studyDays))
        {
            return [];
        }

        return [.. studyDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
