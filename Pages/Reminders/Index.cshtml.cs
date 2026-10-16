using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Reminders;

/// <summary>
/// The JSON endpoints behind the session reminder card that every signed-in
/// page shows (Pages/Shared/_SessionReminder.cshtml + wwwroot/js/session-reminder.js).
/// The card polls Upcoming once a minute and posts the student's answer to
/// one of the handlers below; the rules themselves live in
/// <see cref="SessionReminderService"/>. Not a page anyone visits.
/// </summary>
public class IndexModel(
    UserManager<ApplicationUser> userManager,
    SessionReminderService reminderService,
    StudySessionService studySessions) : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly SessionReminderService _reminderService = reminderService;
    private readonly StudySessionService _studySessions = studySessions;

    private const string StartFormat = "yyyy-MM-ddTHH:mm";

    public static readonly string[] CancelReasons = ["Too tired", "Ran out of time", "Already covered it", "Something came up", "Other"];

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnGetUpcomingAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var now = DateTime.Now;
        var task = await _reminderService.GetDueReminderAsync(user, now);
        if (task == null)
        {
            return new JsonResult(new { session = (object?)null });
        }

        var pushBack = await _reminderService.GetPushBackOptionsAsync(task);
        var slots = await _reminderService.GetFreeSlotsAsync(task, now);
        var end = task.ScheduledStart.AddMinutes(task.PlannedMinutes);
        var assessment = task.Assessment;

        return new JsonResult(new
        {
            session = new
            {
                id = task.Id,
                title = task.Title,
                courseTitle = assessment?.Course?.Title,
                colour = assessment?.Course?.ColourHex ?? FolderColours.DefaultHex,
                timeLabel = $"{task.ScheduledStart:h:mm} – {end:h:mm tt} · {task.PlannedMinutes} min",
                forLabel = assessment == null
                    ? null
                    : assessment.DueDate is DateTime due ? $"for {assessment.Title}, due {due:ddd d MMM}" : $"for {assessment.Title}",
                minutesUntil = (int)Math.Round((task.ScheduledStart - now).TotalMinutes),
                start = task.ScheduledStart.ToString(StartFormat, CultureInfo.InvariantCulture),
                tutorUrl = Url.Page("/Tutor/Index", new { courseId = assessment?.CourseId }),
                pushBack = pushBack.Select(ToJson),
                slots = slots.Select(ToJson),
                reasons = CancelReasons
            }
        });
    }

    /// <summary>Push back, move to a free slot, or undo a move.</summary>
    public async Task<IActionResult> OnPostMoveAsync([FromForm] int taskId, [FromForm] string start)
    {
        var (user, task) = await LoadAsync(taskId);
        if (task == null)
        {
            return NotFoundJson();
        }

        if (!DateTime.TryParseExact(start, StartFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var newStart))
        {
            return BadRequest(new { error = "Invalid time." });
        }

        var previousStart = task.ScheduledStart;
        var error = await _reminderService.MoveAsync(task, newStart, DateTime.Now);
        if (error != null)
        {
            return new JsonResult(new { ok = false, error });
        }

        return new JsonResult(new
        {
            ok = true,
            previousStart = previousStart.ToString(StartFormat, CultureInfo.InvariantCulture),
            leadMinutes = user!.ReminderLeadMinutes
        });
    }

    /// <summary>"OK, continue": stop reminding and start this session's
    /// study timer (the bar at the top of every page).</summary>
    public async Task<IActionResult> OnPostContinueAsync([FromForm] int taskId)
    {
        var (user, task) = await LoadAsync(taskId);
        if (task == null)
        {
            return NotFoundJson();
        }

        await _reminderService.DismissAsync(task);
        await _studySessions.StartAsync(user!.Id, task.Id, null, DateTime.UtcNow);
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostCancelAsync([FromForm] int taskId, [FromForm] string? reason, [FromForm] string? note)
    {
        var (_, task) = await LoadAsync(taskId);
        if (task == null)
        {
            return NotFoundJson();
        }

        await _reminderService.CancelAsync(task, CancelReasons.Contains(reason) ? reason : null, note);
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostUncancelAsync([FromForm] int taskId)
    {
        var (_, task) = await LoadAsync(taskId);
        if (task == null)
        {
            return NotFoundJson();
        }

        await _reminderService.UncancelAsync(task);
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostDismissAsync([FromForm] int taskId)
    {
        var (_, task) = await LoadAsync(taskId);
        if (task == null)
        {
            return NotFoundJson();
        }

        await _reminderService.DismissAsync(task);
        return new JsonResult(new { ok = true });
    }

    private async Task<(ApplicationUser? User, CalendarTask? Task)> LoadAsync(int taskId)
    {
        var user = await _userManager.GetUserAsync(User);
        return user == null ? (null, null) : (user, await _reminderService.FindAsync(user.Id, taskId));
    }

    private JsonResult NotFoundJson() =>
        new(new { ok = false, error = "That study session no longer exists - it may have been cancelled on the Calendar." }) { StatusCode = 404 };

    private static object ToJson(MoveOption option) => new
    {
        start = option.Start.ToString(StartFormat, CultureInfo.InvariantCulture),
        label = option.Label,
        moveLabel = option.MoveLabel,
        whenLabel = option.WhenLabel,
        detail = option.Detail
    };
}
