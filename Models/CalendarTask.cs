namespace TimeManagement.Models;

/// <summary>
/// A scheduled block of study/work time, optionally tied to an assessment.
/// Not surfaced by any page yet - this table exists ahead of the future
/// Calendar page so its data model lands alongside the rest of this sprint's
/// schema changes.
/// </summary>
public class CalendarTask
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public int? AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime ScheduledStart { get; set; }

    public int PlannedMinutes { get; set; }

    public int? ActualMinutes { get; set; }

    public bool IsCompleted { get; set; }

    public string? MissedReason { get; set; }
}
