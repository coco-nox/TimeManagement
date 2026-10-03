using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Calendar;

/// <summary>
/// The weekly Calendar page: a paintable Available/Blocked grid, the AI
/// schedule generator that turns that availability plus study preferences
/// and assessment due dates into concrete CalendarTask sessions (the Week
/// tab), and the study-time settings that steer it (the Preferences tab).
/// </summary>
public class IndexModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    CalendarSchedulingService calendarSchedulingService) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly CalendarSchedulingService _calendarSchedulingService = calendarSchedulingService;

    private static readonly string[] KnownTabs = ["week", "preferences"];

    // The Week tab's fetch() sends cellsJson as JS object literals
    // ({date, hour}), so the property names arrive lowercase - JsonSerializer
    // is case-sensitive by default and would otherwise silently bind every
    // PaintCellDto to its default values (Date: null, Hour: 0) instead of
    // erroring, which is exactly what let painted cells look fine in the UI
    // but never actually save.
    private static readonly JsonSerializerOptions CellJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // The grid shows 6 AM - 11 PM (the last row covers 10 PM-11 PM). Chosen
    // as a reasonable "student day" - wide enough to cover early risers and
    // night owners without painting a full, mostly-unused 24-hour grid.
    public const int GridStartHour = 6;
    public const int GridEndHour = 22;

    public string ActiveTab { get; set; } = "week";

    public int WeekOffset { get; set; }

    public DateTime WeekStart { get; set; }

    public List<Course> Courses { get; set; } = [];

    /// <summary>Every cell that has something to show this week - painted
    /// Available/Blocked hours plus fixed CalendarTask hours - keyed by
    /// "yyyy-MM-dd_H" so the Razor grid can look each cell up in O(1)
    /// instead of scanning a list per cell.</summary>
    public Dictionary<string, CalendarCellView> CellsByKey { get; set; } = [];

    public int AvailableHours { get; set; }

    public int BlockedHours { get; set; }

    // ---- Preferences tab state -------------------------------------------

    public int TotalWeeklyHours { get; set; } = 10;

    public List<string> SelectedStudyDays { get; set; } = [];

    /// <summary>One entry per course the student has, in the same order as
    /// Courses, so the Preferences tab can render one slider per course even
    /// for courses that have never had an hours preference saved.</summary>
    public List<CourseHoursView> CourseHours { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public static string CellKey(DateTime date, int hour) => $"{date:yyyy-MM-dd}_{hour}";

    public async Task<IActionResult> OnGetAsync(int weekOffset = 0, string tab = "week")
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        ActiveTab = KnownTabs.Contains(tab) ? tab : "week";
        WeekOffset = weekOffset;
        WeekStart = GetWeekStart(weekOffset);

        Courses = await _db.Courses
            .Where(c => c.UserId == user.Id)
            .OrderBy(c => c.Title)
            .ToListAsync();

        await LoadWeekAsync(user.Id);
        await LoadPreferencesAsync(user.Id);

        return Page();
    }

    /// <summary>
    /// Called by the Week tab's fetch() once per finished drag (mouse
    /// released), painting or clearing every cell the drag touched in one
    /// batch rather than one request per cell. "outcome" is decided
    /// client-side from the cell the drag started on: "set" applies `kind`
    /// to every touched cell (this is what makes dragging Blocked over an
    /// already-Available cell replace it - see the AvailabilityBlock lookup
    /// below, which always updates the one existing row for a cell instead
    /// of inserting a second one), "clear" removes whatever row a cell has.
    /// Cells covered by a fixed CalendarTask are silently skipped even if a
    /// tampered request includes them - scheduled hours can't be painted
    /// over here.
    /// </summary>
    public async Task<IActionResult> OnPostPaintCellsAsync(
        [FromForm] string weekStart,
        [FromForm] string kind,
        [FromForm] string outcome,
        [FromForm] string cellsJson)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<AvailabilityKind>(kind, ignoreCase: true, out var parsedKind))
        {
            return BadRequest("Invalid kind.");
        }

        if (outcome != "set" && outcome != "clear")
        {
            return BadRequest("Invalid outcome.");
        }

        if (!DateTime.TryParse(weekStart, out var weekStartDate))
        {
            return BadRequest("Invalid week.");
        }
        weekStartDate = weekStartDate.Date;
        var weekEndDate = weekStartDate.AddDays(7);

        List<PaintCellDto>? cells;
        try
        {
            cells = JsonSerializer.Deserialize<List<PaintCellDto>>(cellsJson, CellJsonOptions);
        }
        catch (JsonException)
        {
            return BadRequest("Invalid cells payload.");
        }

        if (cells == null || cells.Count == 0)
        {
            return BadRequest("No cells supplied.");
        }

        // Loaded once up front (not per cell) so rejecting scheduled cells
        // below doesn't run a query per cell in the batch.
        var scheduledCells = await BuildScheduledCellSetAsync(user.Id, weekStartDate, weekEndDate);

        List<object> results = [];

        foreach (var cell in cells.DistinctBy(c => (c.Date, c.Hour)))
        {
            if (!DateTime.TryParse(cell.Date, out var cellDate) || cell.Hour < GridStartHour || cell.Hour > GridEndHour)
            {
                continue;
            }
            cellDate = cellDate.Date;

            // Fixed: rescheduling a CalendarTask happens from the
            // Course/Assessment page, not by painting over it here.
            if (scheduledCells.Contains((cellDate, cell.Hour)))
            {
                continue;
            }

            var existing = await _db.AvailabilityBlocks
                .FirstOrDefaultAsync(b => b.UserId == user.Id && b.Date == cellDate && b.Hour == cell.Hour);

            string resultKind;
            if (outcome == "clear")
            {
                if (existing != null)
                {
                    _db.AvailabilityBlocks.Remove(existing);
                }
                resultKind = "None";
            }
            else
            {
                // Upsert in place - this cell can only ever have one row, so
                // painting the opposite kind over it replaces the Kind on
                // this same row rather than stacking a second one.
                if (existing == null)
                {
                    _db.AvailabilityBlocks.Add(new AvailabilityBlock
                    {
                        UserId = user.Id,
                        Date = cellDate,
                        Hour = cell.Hour,
                        Kind = parsedKind
                    });
                }
                else
                {
                    existing.Kind = parsedKind;
                }
                resultKind = parsedKind.ToString();
            }

            results.Add(new { date = cell.Date, hour = cell.Hour, kind = resultKind });
        }

        await _db.SaveChangesAsync();

        // Recomputed from the database, not carried forward from client
        // state, so the counters reflect what actually saved.
        var weekBlocks = await _db.AvailabilityBlocks
            .Where(b => b.UserId == user.Id && b.Date >= weekStartDate && b.Date < weekEndDate)
            .ToListAsync();

        return new JsonResult(new
        {
            results,
            availableHours = weekBlocks.Count(b => b.Kind == AvailabilityKind.Available),
            blockedHours = weekBlocks.Count(b => b.Kind == AvailabilityKind.Blocked)
        });
    }

    /// <summary>
    /// Builds an AI study plan for the displayed week from the student's
    /// painted availability, their Preferences-tab settings, and their
    /// upcoming Report/Quiz/Test due dates, then saves it as CalendarTask
    /// rows. Always replaces this week's previous AI plan first (see the
    /// RemoveRange below) rather than adding to it, so a student who blocks
    /// off newly-unavailable time and regenerates gets a plan that actually
    /// respects the change, instead of old sessions surviving untouched next
    /// to new ones. The AI is instructed not to pad the week just because
    /// hours are free (see CalendarSchedulingService's system prompt), and
    /// AcceptSessions below enforces that as a hard rule regardless of what
    /// the AI actually returns: nothing gets saved outside the student's own
    /// painted-available cells, and nothing pushes total or per-course
    /// minutes past what they asked for in Preferences.
    /// </summary>
    public async Task<IActionResult> OnPostGenerateScheduleAsync(int weekOffset)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var weekStart = GetWeekStart(weekOffset);
        var weekEnd = weekStart.AddDays(7);

        var preference = await _db.StudyPreferences.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (preference == null)
        {
            ErrorMessage = "Set your study preferences first - how many hours you want to study and how many hours to spend on each course.";
            return RedirectToPage(new { weekOffset, tab = "week" });
        }

        var existingTasks = await _db.CalendarTasks
            .Where(t => t.UserId == user.Id && t.ScheduledStart >= weekStart && t.ScheduledStart < weekEnd)
            .ToListAsync();
        _db.CalendarTasks.RemoveRange(existingTasks);
        await _db.SaveChangesAsync();

        // Only courses the student actually allocated time to are eligible -
        // a course left at 0 hours/week is a clear "not this week" signal,
        // not something for the AI to second-guess.
        var courseHoursById = await _db.CourseHoursPreferences
            .Where(c => c.UserId == user.Id && c.HoursPerWeek > 0)
            .ToDictionaryAsync(c => c.CourseId, c => c.HoursPerWeek);

        if (courseHoursById.Count == 0)
        {
            ErrorMessage = "Allocate some weekly hours to at least one course in Preferences before generating a schedule.";
            return RedirectToPage(new { weekOffset, tab = "week" });
        }

        var windows = await BuildAvailableWindowsAsync(user.Id, weekStart, weekEnd);
        if (windows.Count == 0)
        {
            ErrorMessage = "Paint some available hours on the calendar before generating a schedule.";
            return RedirectToPage(new { weekOffset, tab = "week" });
        }

        var eligibleCourseIds = courseHoursById.Keys.ToList();
        var assessments = await _db.Assessments
            .Include(a => a.Course)
            .Where(a => eligibleCourseIds.Contains(a.CourseId)
                && a.Course != null && a.Course.UserId == user.Id
                && a.Category != AssessmentCategory.Coursework
                && !a.IsCompleted
                && a.DueDate != null)
            .OrderBy(a => a.DueDate)
            .ToListAsync();

        if (assessments.Count == 0)
        {
            ErrorMessage = "No upcoming Report, Quiz, or Test assessments need study time right now.";
            return RedirectToPage(new { weekOffset, tab = "week" });
        }

        var eligibleAssessments = assessments
            .Select(a => new EligibleAssessment(
                a.Id, a.CourseId, a.Course!.Title, a.Title, a.Category.ToString(), a.DueDate!.Value, courseHoursById[a.CourseId]))
            .ToList();

        var result = await _calendarSchedulingService.GenerateAsync(windows, eligibleAssessments, preference.TotalWeeklyHours);

        if (result.Sessions.Count == 0)
        {
            ErrorMessage = result.Error ?? "The AI didn't find a useful way to fit study time into your available hours this week - your plan may already be realistic as is.";
            return RedirectToPage(new { weekOffset, tab = "week" });
        }

        var availableCells = await BuildAvailableCellSetAsync(user.Id, weekStart, weekEnd);
        var assessmentsById = assessments.ToDictionary(a => a.Id);

        var accepted = AcceptSessions(result.Sessions, availableCells, assessmentsById, courseHoursById, preference.TotalWeeklyHours);

        foreach (var session in accepted)
        {
            _db.CalendarTasks.Add(new CalendarTask
            {
                UserId = user.Id,
                AssessmentId = session.AssessmentId,
                Title = session.Title,
                ScheduledStart = session.Date.AddHours(session.StartHour),
                PlannedMinutes = session.DurationMinutes
            });
        }

        await _db.SaveChangesAsync();

        StatusMessage = accepted.Count > 0
            ? $"Generated {accepted.Count} study session{(accepted.Count == 1 ? "" : "s")} for this week."
            : "The AI's suggested sessions didn't hold up once checked against your available hours and preferences, so nothing was scheduled. Try painting more available time.";

        return RedirectToPage(new { weekOffset, tab = "week" });
    }

    /// <summary>
    /// Saves the Preferences tab as one plain form post (not fetch, unlike
    /// painting) - sliders and day pills don't need a live in-page update,
    /// so this follows the same POST+redirect pattern as the rest of the app.
    /// </summary>
    public async Task<IActionResult> OnPostSavePreferencesAsync(
        [FromForm] int totalWeeklyHours,
        [FromForm] List<string>? selectedDays,
        [FromForm] List<int>? courseIds,
        [FromForm] List<int>? courseHoursPerWeek)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        courseIds ??= [];
        courseHoursPerWeek ??= [];

        if (courseIds.Count != courseHoursPerWeek.Count)
        {
            return BadRequest("Malformed course hours.");
        }

        // Every posted course must belong to this user - a tampered id 404s
        // the whole save rather than silently skipping it or saving it
        // against someone else's course.
        var distinctCourseIds = courseIds.Distinct().ToList();
        var ownedCourseCount = await _db.Courses
            .CountAsync(c => c.UserId == user.Id && distinctCourseIds.Contains(c.Id));

        if (ownedCourseCount != distinctCourseIds.Count)
        {
            return NotFound();
        }

        var preference = await _db.StudyPreferences.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (preference == null)
        {
            preference = new StudyPreference { UserId = user.Id };
            _db.StudyPreferences.Add(preference);
        }

        preference.TotalWeeklyHours = Math.Clamp(totalWeeklyHours, 0, 168);
        preference.StudyDays = StudyDayNames.Normalize(selectedDays);

        for (var i = 0; i < courseIds.Count; i++)
        {
            var courseId = courseIds[i];
            var hours = Math.Clamp(courseHoursPerWeek[i], 0, 168);

            var row = await _db.CourseHoursPreferences
                .FirstOrDefaultAsync(c => c.UserId == user.Id && c.CourseId == courseId);

            if (row == null)
            {
                row = new CourseHoursPreference { UserId = user.Id, CourseId = courseId };
                _db.CourseHoursPreferences.Add(row);
            }

            row.HoursPerWeek = hours;
        }

        await _db.SaveChangesAsync();

        StatusMessage = "Preferences saved.";
        return RedirectToPage(new { tab = "preferences" });
    }

    private async Task LoadWeekAsync(string userId)
    {
        var weekEnd = WeekStart.AddDays(7);

        var blocks = await _db.AvailabilityBlocks
            .Where(b => b.UserId == userId && b.Date >= WeekStart && b.Date < weekEnd)
            .ToListAsync();

        AvailableHours = blocks.Count(b => b.Kind == AvailabilityKind.Available);
        BlockedHours = blocks.Count(b => b.Kind == AvailabilityKind.Blocked);

        CellsByKey = [];
        foreach (var block in blocks)
        {
            CellsByKey[CellKey(block.Date, block.Hour)] = new CalendarCellView(
                block.Kind == AvailabilityKind.Available ? "available" : "blocked",
                null,
                null);
        }

        // Scheduled tasks are drawn on top - painted cells "underneath" a
        // task (from before it was scheduled) still exist in the database,
        // but the grid always shows the fixed task, never the paint.
        var tasks = await _db.CalendarTasks
            .Include(t => t.Assessment)
                .ThenInclude(a => a!.Course)
            .Where(t => t.UserId == userId && t.ScheduledStart >= WeekStart && t.ScheduledStart < weekEnd)
            .ToListAsync();

        foreach (var task in tasks)
        {
            var courseColour = task.Assessment?.Course?.ColourHex ?? FolderColours.DefaultHex;
            foreach (var hour in OccupiedHours(task))
            {
                if (hour < GridStartHour || hour > GridEndHour)
                {
                    continue;
                }

                CellsByKey[CellKey(task.ScheduledStart.Date, hour)] = new CalendarCellView("scheduled", task.Title, courseColour);
            }
        }
    }

    private async Task LoadPreferencesAsync(string userId)
    {
        var preference = await _db.StudyPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        TotalWeeklyHours = preference?.TotalWeeklyHours ?? 10;
        SelectedStudyDays = StudyDayNames.Parse(preference?.StudyDays ?? "Mon,Tue,Wed,Thu,Fri");

        var savedHours = await _db.CourseHoursPreferences
            .Where(c => c.UserId == userId)
            .ToDictionaryAsync(c => c.CourseId, c => c.HoursPerWeek);

        CourseHours = [.. Courses.Select(c => new CourseHoursView(c.Id, c.Title, c.ColourHex, savedHours.GetValueOrDefault(c.Id, 0)))];
    }

    /// <summary>Every painted-Available cell in the week that isn't also
    /// covered by a fixed CalendarTask - the ground truth of when the AI is
    /// allowed to place a session. (In practice OnPostGenerateScheduleAsync
    /// always clears this week's tasks before calling this, but the
    /// exclusion is kept here too so this method is correct on its own.)</summary>
    private async Task<HashSet<(DateTime Date, int Hour)>> BuildAvailableCellSetAsync(string userId, DateTime weekStart, DateTime weekEnd)
    {
        var scheduledCells = await BuildScheduledCellSetAsync(userId, weekStart, weekEnd);

        var availableBlocks = await _db.AvailabilityBlocks
            .Where(b => b.UserId == userId && b.Date >= weekStart && b.Date < weekEnd && b.Kind == AvailabilityKind.Available)
            .ToListAsync();

        var cells = new HashSet<(DateTime, int)>();
        foreach (var block in availableBlocks)
        {
            if (!scheduledCells.Contains((block.Date, block.Hour)))
            {
                cells.Add((block.Date, block.Hour));
            }
        }

        return cells;
    }

    /// <summary>Groups each day's available hours into contiguous windows
    /// (e.g. 6 PM-9 PM on Tuesday) rather than handing the AI a flat list of
    /// 100+ individual hour cells - more compact, and easier for it to
    /// reason about as actual blocks of free time.</summary>
    private async Task<List<AvailableWindow>> BuildAvailableWindowsAsync(string userId, DateTime weekStart, DateTime weekEnd)
    {
        var cells = await BuildAvailableCellSetAsync(userId, weekStart, weekEnd);

        var windows = new List<AvailableWindow>();
        var dates = cells.Select(c => c.Date).Distinct().OrderBy(d => d);

        foreach (var date in dates)
        {
            var hours = cells.Where(c => c.Date == date).Select(c => c.Hour).OrderBy(h => h).ToList();

            var rangeStart = hours[0];
            var previous = hours[0];

            for (var i = 1; i <= hours.Count; i++)
            {
                if (i < hours.Count && hours[i] == previous + 1)
                {
                    previous = hours[i];
                    continue;
                }

                windows.Add(new AvailableWindow(date, rangeStart, previous + 1));

                if (i < hours.Count)
                {
                    rangeStart = hours[i];
                    previous = hours[i];
                }
            }
        }

        return windows;
    }

    /// <summary>
    /// Filters and trims the AI's suggested sessions down to ones that are
    /// actually safe to save: every hour a session would occupy must be a
    /// real, unclaimed Available cell (never Blocked, never outside the
    /// grid, never double-booked against another accepted session), and
    /// accepting it must not push the assessment's course - or the week as a
    /// whole - past the hour budgets from Preferences. Suggestions are
    /// walked nearest-due-date first, so when the AI overreaches, capacity
    /// goes to the most urgent assessments rather than whichever one
    /// happened to be listed first in its response.
    /// </summary>
    private static List<ScheduleSuggestion> AcceptSessions(
        List<ScheduleSuggestion> suggestions,
        HashSet<(DateTime Date, int Hour)> availableCells,
        Dictionary<int, Assessment> assessmentsById,
        Dictionary<int, int> courseHoursPerWeekById,
        int totalWeeklyHours)
    {
        var totalCapMinutes = totalWeeklyHours * 60;
        var courseCapMinutes = courseHoursPerWeekById.ToDictionary(kv => kv.Key, kv => kv.Value * 60);

        var totalMinutesUsed = 0;
        var courseMinutesUsed = new Dictionary<int, int>();
        var claimedCells = new HashSet<(DateTime, int)>();
        var accepted = new List<ScheduleSuggestion>();

        var ordered = suggestions
            .Where(s => assessmentsById.ContainsKey(s.AssessmentId))
            .OrderBy(s => assessmentsById[s.AssessmentId].DueDate);

        foreach (var suggestion in ordered)
        {
            var assessment = assessmentsById[suggestion.AssessmentId];
            var courseId = assessment.CourseId;
            if (!courseCapMinutes.TryGetValue(courseId, out var courseCap))
            {
                continue;
            }

            var duration = Math.Clamp(suggestion.DurationMinutes, 30, 120);
            var occupiedHours = Math.Max(1, (int)Math.Ceiling(duration / 60.0));

            var cellsNeeded = new List<(DateTime, int)>();
            var fits = true;
            for (var h = suggestion.StartHour; h < suggestion.StartHour + occupiedHours; h++)
            {
                var cell = (suggestion.Date, h);
                if (!availableCells.Contains(cell) || claimedCells.Contains(cell))
                {
                    fits = false;
                    break;
                }
                cellsNeeded.Add(cell);
            }

            if (!fits || totalMinutesUsed + duration > totalCapMinutes)
            {
                continue;
            }

            var courseUsed = courseMinutesUsed.GetValueOrDefault(courseId, 0);
            if (courseUsed + duration > courseCap)
            {
                continue;
            }

            foreach (var cell in cellsNeeded)
            {
                claimedCells.Add(cell);
            }
            totalMinutesUsed += duration;
            courseMinutesUsed[courseId] = courseUsed + duration;

            var title = string.IsNullOrWhiteSpace(suggestion.Title) ? assessment.Title : suggestion.Title;
            accepted.Add(suggestion with { DurationMinutes = duration, Title = title });
        }

        return accepted;
    }

    /// <summary>Every (date, hour) covered by a fixed CalendarTask in the
    /// given week, so the paint handler can reject cells the drag touched
    /// without running a query per cell.</summary>
    private async Task<HashSet<(DateTime Date, int Hour)>> BuildScheduledCellSetAsync(string userId, DateTime weekStart, DateTime weekEnd)
    {
        var tasks = await _db.CalendarTasks
            .Where(t => t.UserId == userId && t.ScheduledStart >= weekStart && t.ScheduledStart < weekEnd)
            .ToListAsync();

        var cells = new HashSet<(DateTime, int)>();
        foreach (var task in tasks)
        {
            foreach (var hour in OccupiedHours(task))
            {
                cells.Add((task.ScheduledStart.Date, hour));
            }
        }

        return cells;
    }

    /// <summary>The hour-of-day slots a task's PlannedMinutes spans, starting
    /// from its ScheduledStart hour (e.g. a 90-minute task starting at 18:00
    /// occupies hours 18 and 19). Always at least one hour.</summary>
    private static IEnumerable<int> OccupiedHours(CalendarTask task)
    {
        var startHour = task.ScheduledStart.Hour;
        var occupiedHours = Math.Max(1, (int)Math.Ceiling(task.PlannedMinutes / 60.0));
        for (var h = startHour; h < startHour + occupiedHours; h++)
        {
            yield return h;
        }
    }

    /// <summary>Monday of the week `offset` weeks from the current one.</summary>
    private static DateTime GetWeekStart(int offset)
    {
        var today = DateTime.Today;
        var daysSinceMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return today.AddDays(-daysSinceMonday).AddDays(offset * 7);
    }
}

/// <summary>One grid cell's display state: "available", "blocked", or
/// "scheduled". Title/CourseColourHex are only set for "scheduled".</summary>
public sealed record CalendarCellView(string State, string? Title, string? CourseColourHex);

/// <summary>One course's Preferences-tab row: its saved (or default 0)
/// weekly hours allocation.</summary>
public sealed record CourseHoursView(int CourseId, string Title, string ColourHex, int HoursPerWeek);

/// <summary>One cell in a paint batch, as sent by the Week tab's fetch().</summary>
public sealed record PaintCellDto(string Date, int Hour);
