namespace TimeManagement.Models;

/// <summary>
/// The Analytics page's AI-written "Your week, summarised" card, saved so
/// reopening the page doesn't cost another AI call. One row per user per
/// week. InputHash fingerprints the numbers and check-in the summary was
/// written from: when the student logs another session or edits their
/// check-in, the hash no longer matches and the summary is rewritten.
/// </summary>
public class WeeklySummary
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>The Monday the summarised week starts on.</summary>
    public DateTime WeekStart { get; set; }

    public string InputHash { get; set; } = string.Empty;

    /// <summary>The validated AI reply as JSON: { "observations": [...], "suggestion": "..." }.</summary>
    public string ContentJson { get; set; } = string.Empty;

    public DateTime GeneratedUtc { get; set; }
}
