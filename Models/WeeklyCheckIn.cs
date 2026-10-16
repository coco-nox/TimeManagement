namespace TimeManagement.Models;

/// <summary>
/// A user's weekly self-reported check-in from the Analytics page's "How was
/// your week?" card: mood, how the week compared to the last one, whether
/// the schedule fit, how prepared they feel for what's coming, and optional
/// free-text feedback. One row per user per week (see the unique index in
/// ApplicationDbContext) - saving again updates it in place.
/// </summary>
public class WeeklyCheckIn
{
    public static readonly string[] MoodOptions = ["Great", "Okay", "Rough", "Overwhelming"];
    public static readonly string[] ComparedOptions = ["Better", "About the same", "Worse"];
    public static readonly string[] ScheduleFitOptions = ["Yes, worked well", "Mostly", "No, too packed"];
    public static readonly string[] PreparednessOptions = ["Ahead", "On track", "Behind"];

    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>The Sunday the check-in's Monday-Sunday week ends on.</summary>
    public DateTime WeekEnding { get; set; }

    public string Mood { get; set; } = string.Empty;

    public string ComparedToLastWeek { get; set; } = string.Empty;

    public string ScheduleFit { get; set; } = string.Empty;

    public string Preparedness { get; set; } = string.Empty;

    public string? Feedback { get; set; }
}
