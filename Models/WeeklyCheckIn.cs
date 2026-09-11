namespace TimeManagement.Models;

/// <summary>
/// A user's weekly self-reported check-in (mood, whether their schedule fit
/// the week, optional free-text feedback). Not surfaced by any page yet -
/// this table exists ahead of the future Analytics page so its data model
/// lands alongside the rest of this sprint's schema changes.
/// </summary>
public class WeeklyCheckIn
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public DateTime WeekEnding { get; set; }

    public string Mood { get; set; } = string.Empty;

    public string ScheduleFit { get; set; } = string.Empty;

    public string? Feedback { get; set; }
}
