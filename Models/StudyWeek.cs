namespace TimeManagement.Models;

/// <summary>The app's Monday-Sunday week, shared by the Calendar and
/// Analytics pages so their "this week" / weekOffset always agree.</summary>
public static class StudyWeek
{
    /// <summary>Monday of the week `offset` weeks from the current one.</summary>
    public static DateTime GetWeekStart(int offset)
    {
        var today = DateTime.Today;
        var daysSinceMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return today.AddDays(-daysSinceMonday).AddDays(offset * 7);
    }
}
