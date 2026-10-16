using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// The rules behind study-session reminders, shared by the in-app reminder
/// card (Pages/Reminders), the reminder email/Telegram job and the Telegram
/// bot's buttons: which session is due a
/// reminder, where it can be moved to when the student needs more time, and
/// what cancelling or dismissing it records.
///
/// A session gets a reminder from <see cref="ApplicationUser.ReminderLeadMinutes"/>
/// before it starts until a little after, unless it's already been logged
/// (Done/Missed on Analytics) or the reminder was answered or closed. All
/// times are local, the same as <see cref="CalendarTask.ScheduledStart"/>.
/// </summary>
public sealed class SessionReminderService(ApplicationDbContext db)
{
    private readonly ApplicationDbContext _db = db;

    /// <summary>How long after a session starts its reminder still shows,
    /// for a student who opens FlightPath a few minutes late.</summary>
    public const int ShowAfterStartMinutes = 10;

    /// <summary>Sessions must end by the Calendar grid's last hour (11 PM).</summary>
    private const int LatestEndHour = 23;

    public static readonly int[] PushBackMinutes = [15, 30, 60];

    public const int MaxReasonLength = 300;

    /// <summary>The one session the in-app reminder should show right now
    /// (the soonest, if more than one is due), or null.</summary>
    public async Task<CalendarTask?> GetDueReminderAsync(ApplicationUser user, DateTime now)
    {
        var showFrom = now.AddMinutes(-ShowAfterStartMinutes);
        var showUntil = now.AddMinutes(user.ReminderLeadMinutes);

        return await _db.CalendarTasks
            .Include(t => t.Assessment).ThenInclude(a => a!.Course)
            .Where(t => t.UserId == user.Id
                && !t.IsCompleted && !t.IsMissed
                && t.ReminderDismissedUtc == null
                && t.ScheduledStart >= showFrom && t.ScheduledStart <= showUntil)
            .OrderBy(t => t.ScheduledStart)
            .FirstOrDefaultAsync();
    }

    public Task<CalendarTask?> FindAsync(string userId, int taskId) =>
        _db.CalendarTasks
            .Include(t => t.Assessment).ThenInclude(a => a!.Course)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == userId);

    /// <summary>"+15 min" style options: the same session pushed back, kept
    /// only when the new time doesn't run into another session or past 11 PM.</summary>
    public async Task<List<MoveOption>> GetPushBackOptionsAsync(CalendarTask task)
    {
        var others = await LoadOtherSessionsAsync(task, task.ScheduledStart.Date, task.ScheduledStart.Date.AddDays(1));
        var options = new List<MoveOption>();

        foreach (var minutes in PushBackMinutes)
        {
            var newStart = task.ScheduledStart.AddMinutes(minutes);
            if (FitsDay(newStart, task.PlannedMinutes) && !Overlaps(newStart, task.PlannedMinutes, others))
            {
                options.Add(new MoveOption(
                    newStart,
                    minutes < 60 ? $"+{minutes} min" : "+1 hour",
                    minutes < 60 ? $"Push back {minutes} min" : "Push back 1 hour",
                    newStart.ToString("h:mm tt"),
                    null));
            }
        }

        return options;
    }

    /// <summary>
    /// Up to <paramref name="max"/> other free slots later the same
    /// Monday-Sunday week: whole hours the student painted Available on the
    /// Calendar, long enough for the session, not covered by another session.
    /// </summary>
    public async Task<List<MoveOption>> GetFreeSlotsAsync(CalendarTask task, DateTime now, int max = 2)
    {
        var weekStart = WeekStartOf(task.ScheduledStart);
        var weekEnd = weekStart.AddDays(7);
        var hoursNeeded = Math.Max(1, (int)Math.Ceiling(task.PlannedMinutes / 60.0));
        var earliest = now.AddMinutes(30);

        var availableCells = (await _db.AvailabilityBlocks
                .Where(b => b.UserId == task.UserId && b.Date >= weekStart && b.Date < weekEnd && b.Kind == AvailabilityKind.Available)
                .Select(b => new { b.Date, b.Hour })
                .ToListAsync())
            .Select(b => (b.Date.Date, b.Hour))
            .ToHashSet();
        var others = await LoadOtherSessionsAsync(task, weekStart, weekEnd);

        var slots = new List<MoveOption>();
        foreach (var date in availableCells.Select(c => c.Date).Distinct().Order())
        {
            var hours = availableCells.Where(c => c.Date == date).Select(c => c.Hour).Order().ToList();
            foreach (var hour in hours)
            {
                var start = date.AddHours(hour);
                if (start < earliest || start == task.ScheduledStart)
                {
                    continue;
                }

                var wholeRunFree = Enumerable.Range(hour, hoursNeeded).All(h => availableCells.Contains((date, h)));
                if (!wholeRunFree || !FitsDay(start, task.PlannedMinutes) || Overlaps(start, task.PlannedMinutes, others))
                {
                    continue;
                }

                // "Free until" = the end of this run of available hours.
                var runEnd = hour;
                while (availableCells.Contains((date, runEnd + 1)))
                {
                    runEnd++;
                }
                var freeUntil = date.AddHours(runEnd + 1);

                var dayLabel = date == now.Date ? "Today" : date == now.Date.AddDays(1) ? "Tomorrow" : date.ToString("dddd");
                slots.Add(new MoveOption(
                    start,
                    $"{dayLabel}, {start:h:mm tt}",
                    $"Move to {dayLabel.ToLowerInvariant()}, {start:h:mm tt}",
                    date == now.Date ? $"{start:h:mm tt} today" : $"{dayLabel}, {start:h:mm tt}",
                    $"Free until {freeUntil:h:mm tt}"));

                if (slots.Count >= max)
                {
                    return slots;
                }

                // One suggestion per run of free hours, not every hour in it.
                break;
            }
        }

        return slots;
    }

    /// <summary>
    /// Moves the session (more time, another free slot, or undoing a move).
    /// Rejects a time that's already over, runs past 11 PM, leaves the
    /// session's week, or overlaps another session. Clears both reminder
    /// timestamps so the new time gets its own reminder and email.
    /// </summary>
    public async Task<string?> MoveAsync(CalendarTask task, DateTime newStart, DateTime now)
    {
        if (task.IsCompleted || task.IsMissed)
        {
            return "This session has already been logged, so it can't be moved.";
        }

        if (newStart.AddMinutes(task.PlannedMinutes) <= now)
        {
            return "That time has already passed.";
        }

        if (WeekStartOf(newStart) != WeekStartOf(task.ScheduledStart))
        {
            return "Sessions can only be moved within the same week.";
        }

        if (!FitsDay(newStart, task.PlannedMinutes))
        {
            return "That would run past 11 PM.";
        }

        var others = await LoadOtherSessionsAsync(task, newStart.Date, newStart.Date.AddDays(1));
        if (Overlaps(newStart, task.PlannedMinutes, others))
        {
            return "That time overlaps another study session.";
        }

        task.ScheduledStart = newStart;
        task.ReminderDismissedUtc = null;
        task.ReminderEmailSentUtc = null;
        task.ReminderTelegramSentUtc = null;
        task.ReminderTelegramMessageId = null;
        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>Cancels from the reminder: logged as Missed on Analytics, with the reason.</summary>
    public async Task CancelAsync(CalendarTask task, string? reason, string? note)
    {
        var parts = new[] { reason?.Trim(), note?.Trim() }.Where(p => !string.IsNullOrEmpty(p));
        var combined = string.Join(" - ", parts);

        task.IsCompleted = false;
        task.IsMissed = true;
        task.ActualMinutes = null;
        task.MissedReason = combined.Length == 0 ? null : combined.Length > MaxReasonLength ? combined[..MaxReasonLength] : combined;
        task.ReminderDismissedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>Undoes <see cref="CancelAsync"/>: back to not logged, reminder showing again.</summary>
    public async Task UncancelAsync(CalendarTask task)
    {
        task.IsMissed = false;
        task.MissedReason = null;
        task.ReminderDismissedUtc = null;
        await _db.SaveChangesAsync();
    }

    /// <summary>"OK, continue" or the close button: stop showing this reminder.</summary>
    public async Task DismissAsync(CalendarTask task)
    {
        task.ReminderDismissedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private Task<List<CalendarTask>> LoadOtherSessionsAsync(CalendarTask task, DateTime from, DateTime to) =>
        _db.CalendarTasks
            .AsNoTracking()
            .Where(t => t.UserId == task.UserId && t.Id != task.Id && t.ScheduledStart >= from && t.ScheduledStart < to)
            .ToListAsync();

    private static bool FitsDay(DateTime start, int plannedMinutes) =>
        start.AddMinutes(plannedMinutes) <= start.Date.AddHours(LatestEndHour);

    private static bool Overlaps(DateTime start, int plannedMinutes, List<CalendarTask> others)
    {
        var end = start.AddMinutes(plannedMinutes);
        return others.Any(o => start < o.ScheduledStart.AddMinutes(o.PlannedMinutes) && o.ScheduledStart < end);
    }

    private static DateTime WeekStartOf(DateTime date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.Date.AddDays(-daysSinceMonday);
    }
}

/// <summary>One place a session could move to. Label is the button text
/// ("+15 min", "Today, 8:00 PM"), MoveLabel the confirm button ("Push back
/// 15 min"), WhenLabel the "Moved to …" confirmation, Detail an optional
/// second line ("Free until 10:00 PM").</summary>
public sealed record MoveOption(DateTime Start, string Label, string MoveLabel, string WhenLabel, string? Detail);
