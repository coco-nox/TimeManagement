using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Analytics;

/// <summary>
/// "Your week in review": how the Calendar's scheduled study sessions
/// actually went (completed vs missed per day, sessions that ran over), a
/// comparison with last week, the student's own weekly check-in, and an
/// AI-written summary. Sessions are logged here too (Done with optional
/// actual minutes, or Missed) - that log is the only source of the
/// completed/missed/ran-over numbers, so a week nobody logged shows as
/// "not logged yet" rather than as missed.
/// </summary>
public class IndexModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    WeeklySummaryService weeklySummaryService) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly WeeklySummaryService _weeklySummaryService = weeklySummaryService;

    private const int MaxActualMinutes = 600;
    private const int MaxFeedbackLength = 1000;

    public int WeekOffset { get; set; }

    public DateTime WeekStart { get; set; }

    public WeekReviewFacts Facts { get; set; } = null!;

    /// <summary>This week's sessions in time order, for the log list.</summary>
    public List<SessionLogView> Sessions { get; set; } = [];

    public WeeklyCheckIn? CheckIn { get; set; }

    /// <summary>Calendar sessions this week that were timed with the session
    /// timer - the rest of the Done ones count as "marked done without the timer".</summary>
    public HashSet<int> TimedTaskIds { get; set; } = [];

    /// <summary>Planned minutes across this week's Calendar sessions.</summary>
    public int ScheduledMinutes => Sessions.Sum(s => s.Task.PlannedMinutes);

    /// <summary>Calendar sessions marked done without starting the timer.</summary>
    public List<CalendarTask> UntimedDoneTasks => [.. Sessions.Select(s => s.Task).Where(t => t.IsCompleted && !TimedTaskIds.Contains(t.Id))];

    /// <summary>"3h 40m" / "45m".</summary>
    public static string Duration(int minutes) => minutes >= 60 ? $"{minutes / 60}h{(minutes % 60 > 0 ? $" {minutes % 60}m" : "")}" : $"{minutes}m";

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public bool IsCurrentWeek => WeekOffset == 0;

    public int CompletionPercent => Facts.SessionsScheduled == 0
        ? 0
        : (int)Math.Round(100.0 * Facts.SessionsCompleted / Facts.SessionsScheduled);

    public async Task<IActionResult> OnGetAsync(int weekOffset = 0)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        WeekOffset = weekOffset;
        WeekStart = StudyWeek.GetWeekStart(weekOffset);

        var tasks = await LoadWeekTasksAsync(user.Id, WeekStart);
        Sessions = [.. tasks.Select(t => new SessionLogView(t, t.Assessment?.Course?.ColourHex ?? FolderColours.DefaultHex))];
        CheckIn = await LoadCheckInAsync(user.Id, WeekStart);
        Facts = await BuildFactsAsync(user.Id, WeekStart, tasks, CheckIn);

        return Page();
    }

    /// <summary>
    /// Fetched by the page's script once the page has loaded, so the AI
    /// call never holds up the rest of the page. Rebuilds the same facts
    /// the page shows rather than trusting anything sent from the browser.
    /// </summary>
    public async Task<IActionResult> OnGetSummaryAsync(int weekOffset = 0)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var weekStart = StudyWeek.GetWeekStart(weekOffset);
        var tasks = await LoadWeekTasksAsync(user.Id, weekStart);
        var checkIn = await LoadCheckInAsync(user.Id, weekStart);
        var facts = await BuildFactsAsync(user.Id, weekStart, tasks, checkIn);

        var result = await _weeklySummaryService.GetOrCreateAsync(user.Id, facts, HttpContext.RequestAborted);
        return new JsonResult(new
        {
            observations = result.Content?.Observations,
            suggestion = result.Content?.Suggestion,
            message = result.Message
        });
    }

    /// <summary>Logs one session as done (optionally with how long it
    /// actually took) or missed, or clears its log back to "not logged".</summary>
    public async Task<IActionResult> OnPostLogSessionAsync(
        [FromForm] int taskId,
        [FromForm] string outcome,
        [FromForm] int? actualMinutes,
        [FromForm] int weekOffset)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var task = await _db.CalendarTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == user.Id);
        if (task == null)
        {
            ErrorMessage = "That study session no longer exists - it may have been cancelled on the Calendar.";
            return RedirectToPage(null, null, new { weekOffset }, "sessions");
        }

        if (actualMinutes is < 1 or > MaxActualMinutes)
        {
            ErrorMessage = $"Actual time should be between 1 and {MaxActualMinutes} minutes.";
            return RedirectToPage(null, null, new { weekOffset }, "sessions");
        }

        switch (outcome)
        {
            case "done":
                task.IsCompleted = true;
                task.IsMissed = false;
                task.ActualMinutes = actualMinutes;
                break;
            case "missed":
                task.IsCompleted = false;
                task.IsMissed = true;
                task.ActualMinutes = null;
                break;
            case "clear":
                task.IsCompleted = false;
                task.IsMissed = false;
                task.ActualMinutes = null;
                break;
            default:
                return BadRequest("Invalid outcome.");
        }

        await _db.SaveChangesAsync();
        return RedirectToPage(null, null, new { weekOffset }, "sessions");
    }

    /// <summary>Saves the week's check-in, updating it in place if the
    /// student already saved one for this week.</summary>
    public async Task<IActionResult> OnPostSaveCheckInAsync(
        [FromForm] int weekOffset,
        [FromForm] string? mood,
        [FromForm] string? comparedToLastWeek,
        [FromForm] string? scheduleFit,
        [FromForm] string? preparedness,
        [FromForm] string? feedback)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (!WeeklyCheckIn.MoodOptions.Contains(mood) ||
            !WeeklyCheckIn.ComparedOptions.Contains(comparedToLastWeek) ||
            !WeeklyCheckIn.ScheduleFitOptions.Contains(scheduleFit) ||
            !WeeklyCheckIn.PreparednessOptions.Contains(preparedness))
        {
            ErrorMessage = "Pick an answer for each of the four questions before saving.";
            return RedirectToPage(null, null, new { weekOffset }, "checkin");
        }

        var weekStart = StudyWeek.GetWeekStart(weekOffset);
        var checkIn = await LoadCheckInAsync(user.Id, weekStart);
        if (checkIn == null)
        {
            checkIn = new WeeklyCheckIn { UserId = user.Id, WeekEnding = weekStart.AddDays(6) };
            _db.WeeklyCheckIns.Add(checkIn);
        }

        checkIn.Mood = mood!;
        checkIn.ComparedToLastWeek = comparedToLastWeek!;
        checkIn.ScheduleFit = scheduleFit!;
        checkIn.Preparedness = preparedness!;
        var trimmedFeedback = feedback?.Trim();
        checkIn.Feedback = string.IsNullOrEmpty(trimmedFeedback)
            ? null
            : trimmedFeedback.Length > MaxFeedbackLength ? trimmedFeedback[..MaxFeedbackLength] : trimmedFeedback;

        await _db.SaveChangesAsync();
        StatusMessage = "Saved your check-in for this week.";
        return RedirectToPage(null, null, new { weekOffset }, "checkin");
    }

    private Task<List<CalendarTask>> LoadWeekTasksAsync(string userId, DateTime weekStart)
    {
        var weekEnd = weekStart.AddDays(7);
        return _db.CalendarTasks
            .Include(t => t.Assessment).ThenInclude(a => a!.Course)
            .Where(t => t.UserId == userId && t.ScheduledStart >= weekStart && t.ScheduledStart < weekEnd)
            .OrderBy(t => t.ScheduledStart)
            .ToListAsync();
    }

    private Task<WeeklyCheckIn?> LoadCheckInAsync(string userId, DateTime weekStart)
    {
        var weekEnding = weekStart.AddDays(6);
        return _db.WeeklyCheckIns.FirstOrDefaultAsync(c => c.UserId == userId && c.WeekEnding == weekEnding);
    }

    private async Task<WeekReviewFacts> BuildFactsAsync(string userId, DateTime weekStart, List<CalendarTask> tasks, WeeklyCheckIn? checkIn)
    {
        var days = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = weekStart.AddDays(offset);
                var dayTasks = tasks.Where(t => t.ScheduledStart.Date == date).ToList();
                return new WeekReviewDay(
                    date,
                    dayTasks.Count,
                    dayTasks.Count(t => t.IsCompleted),
                    dayTasks.Count(t => t.IsMissed),
                    [.. dayTasks.Where(t => t.IsMissed).Select(t => string.IsNullOrEmpty(t.MissedReason) ? t.Title : $"{t.Title} - reason: {t.MissedReason}")]);
            })
            .ToList();

        var overruns = tasks
            .Where(t => t.IsCompleted && t.ActualMinutes > t.PlannedMinutes)
            .Select(t => new WeekReviewOverrun(t.Title, t.ScheduledStart.Date, t.PlannedMinutes, t.ActualMinutes!.Value))
            .ToList();

        var previousWeekStart = weekStart.AddDays(-7);
        var previousWeekTasks = await _db.CalendarTasks
            .Where(t => t.UserId == userId && t.ScheduledStart >= previousWeekStart && t.ScheduledStart < weekStart)
            .Select(t => t.IsCompleted)
            .ToListAsync();

        // Report/Quiz/Test due within two weeks of the week's end - what
        // "how prepared do you feel for what's coming up" is about.
        var horizon = weekStart.AddDays(21);
        var upcoming = await _db.Assessments
            .Include(a => a.Course)
            .Where(a => a.Course != null && a.Course.UserId == userId
                && a.Category != AssessmentCategory.Coursework
                && !a.IsCompleted
                && a.DueDate != null && a.DueDate >= weekStart && a.DueDate < horizon)
            .OrderBy(a => a.DueDate)
            .Take(5)
            .ToListAsync();

        // Timed sessions started this week (local week, stored in UTC).
        var weekStartUtc = weekStart.ToUniversalTime();
        var weekEndUtc = weekStart.AddDays(7).ToUniversalTime();
        var timed = await _db.StudySessions
            .Where(s => s.UserId == userId && s.StartedUtc >= weekStartUtc && s.StartedUtc < weekEndUtc)
            .OrderBy(s => s.StartedUtc)
            .ToListAsync();
        static int Min(int seconds) => (int)Math.Round(seconds / 60.0);
        var timedSessions = timed
            .Select(s => new WeekReviewTimedSession(
                s.Title,
                s.StartedUtc.ToLocalTime(),
                s.PlannedMinutes,
                Min(s.ActiveSeconds),
                Min(s.OfflineSeconds),
                Min(s.InactiveSeconds),
                s.WordsWritten,
                s.ChecklistTicked,
                s.QuizAnswered,
                s.QuizCorrect,
                s.TutorQuestions,
                s.EndedUtc == null))
            .ToList();
        TimedTaskIds = [.. timed.Where(s => s.CalendarTaskId != null).Select(s => s.CalendarTaskId!.Value)];

        return new WeekReviewFacts(
            weekStart,
            tasks.Count,
            tasks.Count(t => t.IsCompleted),
            tasks.Count(t => t.IsMissed),
            tasks.Count(t => !t.IsCompleted && !t.IsMissed),
            previousWeekTasks.Count(completed => completed),
            previousWeekTasks.Count,
            days,
            overruns,
            [.. upcoming.Select(a => $"{a.Title} ({a.Category}, {a.Course!.Title}) due {a.DueDate:ddd d MMM}")],
            checkIn,
            timedSessions);
    }
}

/// <summary>One session in the log list, with its course colour.</summary>
public sealed record SessionLogView(CalendarTask Task, string ColourHex);
