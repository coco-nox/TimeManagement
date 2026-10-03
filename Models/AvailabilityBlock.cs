namespace TimeManagement.Models;

public enum AvailabilityKind
{
    Available,
    Blocked
}

/// <summary>
/// One painted hour cell on a student's weekly Calendar grid: whether they
/// marked that hour as available to study or blocked it off entirely. One
/// row per (UserId, Date, Hour) - see ApplicationDbContext's unique index -
/// so painting the opposite kind over an existing cell always updates this
/// same row in place rather than adding a second one. There's no "empty"
/// Kind value; an hour with neither is simply the absence of a row.
/// </summary>
public class AvailabilityBlock
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>The calendar date this cell belongs to (time component
    /// always midnight) - a real date, not just a day-of-week name, so the
    /// same weekday slot doesn't get reused across different weeks.</summary>
    public DateTime Date { get; set; }

    /// <summary>Hour of day, 0-23. See Pages/Calendar/Index.cshtml.cs for the
    /// grid's displayed hour range.</summary>
    public int Hour { get; set; }

    public AvailabilityKind Kind { get; set; }
}
