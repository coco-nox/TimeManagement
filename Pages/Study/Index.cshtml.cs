using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Study;

/// <summary>
/// The endpoints behind the study-session timer bar that every signed-in
/// page shows (Pages/Shared/_StudySession.cshtml + wwwroot/js/study-session.js):
/// start, heartbeat, the "Still studying?" answer, breaks, end and the
/// end-of-session summary. The rules live in <see cref="StudySessionService"/>.
/// Start also works as a plain form post (Dashboard / Tutor buttons) when
/// given a returnUrl. Not a page anyone visits.
/// </summary>
public class IndexModel(UserManager<ApplicationUser> userManager, StudySessionService sessions) : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly StudySessionService _sessions = sessions;

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnGetCurrentAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var session = await _sessions.GetActiveAsync(user.Id, DateTime.UtcNow);
        return new JsonResult(new { session = session == null ? null : State(session) });
    }

    public async Task<IActionResult> OnPostStartAsync([FromForm] int? taskId, [FromForm] int? courseId, [FromForm] string? returnUrl)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var session = await _sessions.StartAsync(user.Id, taskId, courseId, DateTime.UtcNow);

        if (!string.IsNullOrEmpty(returnUrl))
        {
            return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");
        }

        return new JsonResult(new { session = State(session) });
    }

    public Task<IActionResult> OnPostHeartbeatAsync([FromForm] int sessionId, [FromForm] bool active) =>
        WithSessionAsync(sessionId, (s, now) => _sessions.HeartbeatAsync(s, active, now));

    /// <summary>"Still studying?" - choice is "offline" or "break".</summary>
    public Task<IActionResult> OnPostAnswerAsync([FromForm] int sessionId, [FromForm] string choice) =>
        WithSessionAsync(sessionId, (s, now) => _sessions.AnswerCheckAsync(s, choice == "offline", now));

    public Task<IActionResult> OnPostBreakAsync([FromForm] int sessionId) =>
        WithSessionAsync(sessionId, (s, now) => _sessions.StartBreakAsync(s, now));

    public Task<IActionResult> OnPostResumeAsync([FromForm] int sessionId) =>
        WithSessionAsync(sessionId, (s, now) => _sessions.EndBreakAsync(s, now));

    public async Task<IActionResult> OnPostEndAsync([FromForm] int sessionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var session = await _sessions.GetActiveAsync(user.Id, DateTime.UtcNow);
        if (session == null || session.Id != sessionId)
        {
            return new JsonResult(new { ok = false, error = "That study session has already ended." }) { StatusCode = 404 };
        }

        await _sessions.EndAsync(session, DateTime.UtcNow);
        return new JsonResult(new { ok = true, summary = Summary(session) });
    }

    /// <summary>"Save to Analytics" on the summary, optionally re-counting
    /// some inactive minutes as offline study ("Adjust times").</summary>
    public async Task<IActionResult> OnPostSaveAsync([FromForm] int sessionId, [FromForm] int offlineMinutes)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var session = await _sessions.FindEndedAsync(user.Id, sessionId);
        if (session == null)
        {
            return new JsonResult(new { ok = false, error = "That study session couldn't be found." }) { StatusCode = 404 };
        }

        await _sessions.SaveAsync(session, offlineMinutes);
        return new JsonResult(new { ok = true, analyticsUrl = Url.Page("/Analytics/Index", null, null, null, null, "studied") });
    }

    private async Task<IActionResult> WithSessionAsync(int sessionId, Func<StudySession, DateTime, Task> action)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        var session = await _sessions.GetActiveAsync(user.Id, now);
        if (session == null || session.Id != sessionId)
        {
            // Ended elsewhere (another tab, or it went stale) - tell the page.
            return new JsonResult(new { session = (object?)null });
        }

        await action(session, now);
        return new JsonResult(new { session = State(session) });
    }

    private static object State(StudySession s) => new
    {
        id = s.Id,
        title = s.Title,
        courseTitle = s.Course?.Title,
        plannedMinutes = s.PlannedMinutes,
        elapsedSeconds = (int)Math.Max(0, (DateTime.UtcNow - s.StartedUtc).TotalSeconds),
        studiedSeconds = s.StudiedSeconds,
        inactiveSeconds = s.InactiveSeconds,
        pendingSeconds = s.PendingIdleSeconds,
        isOnBreak = s.IsOnBreak,
        needsCheck = StudySessionService.NeedsCheck(s),
        pendingMinutes = (int)Math.Round(s.PendingIdleSeconds / 60.0)
    };

    private static object Summary(StudySession s) => new
    {
        id = s.Id,
        title = s.Title,
        courseTitle = s.Course?.Title,
        plannedMinutes = s.PlannedMinutes,
        totalMinutes = Minutes(s.TotalSeconds),
        activeMinutes = Minutes(s.ActiveSeconds),
        offlineMinutes = Minutes(s.OfflineSeconds),
        inactiveMinutes = Minutes(s.InactiveSeconds),
        wordsWritten = s.WordsWritten,
        checklistTicked = s.ChecklistTicked,
        quizAnswered = s.QuizAnswered,
        quizCorrect = s.QuizCorrect,
        tutorQuestions = s.TutorQuestions,
        hasCalendarSession = s.CalendarTaskId != null
    };

    private static int Minutes(int seconds) => (int)Math.Round(seconds / 60.0);
}
