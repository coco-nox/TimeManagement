namespace TimeManagement.Models;

/// <summary>
/// How many hours per week a student wants to allocate to one course, set on
/// the Calendar page's Preferences tab. One row per (UserId, CourseId) - see
/// ApplicationDbContext's unique index.
/// </summary>
public class CourseHoursPreference
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public int CourseId { get; set; }

    public Course? Course { get; set; }

    public int HoursPerWeek { get; set; }
}
