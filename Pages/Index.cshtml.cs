using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;

namespace TimeManagement.Pages;

/// <summary>
/// The signed-in home page ("Dashboard" in the sidebar): a folder-card grid
/// of the user's courses, overall assessment progress, and a preview of
/// what's due this week. Signed-out visitors instead see the marketing hero
/// section built directly into Index.cshtml.
/// </summary>
public class IndexModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;

    public List<Course> Courses { get; set; } = [];

    /// <summary>Only Report/Quiz/Test assessments count here - matches the
    /// sidebar's "assessments tracked" tally and the Courses page (see
    /// Pages/Courses/Index.cshtml.cs and Pages/Tutor/Index.cshtml.cs).</summary>
    public int TrackedAssessmentCount { get; set; }

    public int CompletedAssessmentCount { get; set; }

    public int ProgressPercent { get; set; }

    public List<DashboardWeekDay> WeekDays { get; set; } = [];

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            // Signed-out visitors render the hero section instead - nothing
            // here is used in that branch of Index.cshtml.
            return;
        }

        Courses = await _db.Courses
            .Include(c => c.Assessments)
                .ThenInclude(a => a.Documents)
            .Where(c => c.UserId == user.Id)
            .OrderBy(c => c.Title)
            .ToListAsync();

        var trackedAssessments = Courses
            .SelectMany(c => c.Assessments)
            .Where(a => a.Category != AssessmentCategory.Coursework)
            .ToList();

        TrackedAssessmentCount = trackedAssessments.Count;
        CompletedAssessmentCount = trackedAssessments.Count(a => a.IsCompleted);
        ProgressPercent = TrackedAssessmentCount > 0
            ? (int)Math.Round(CompletedAssessmentCount * 100.0 / TrackedAssessmentCount)
            : 0;

        WeekDays = BuildWeekDays(trackedAssessments);
    }

    /// <summary>
    /// Changes one course's Dashboard folder colour. courseId is NOT
    /// [FromForm] - the swatch buttons get it from asp-route-courseId in
    /// the URL, the same reasoning as the Tutor page's archive handlers
    /// (see Pages/Tutor/Index.cshtml.cs) - a form field would silently
    /// bind 0 instead.
    /// </summary>
    public async Task<IActionResult> OnPostUpdateCourseColourAsync(int courseId, [FromForm] string colour)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        // Never trust what comes back from the form: if it isn't one of the
        // standard colours, ignore the request rather than saving it.
        if (!FolderColours.IsValid(colour))
        {
            return BadRequest("Not a valid folder colour.");
        }

        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId && c.UserId == user.Id);
        if (course == null)
        {
            return NotFound();
        }

        course.ColourHex = colour;
        await _db.SaveChangesAsync();

        return RedirectToPage();
    }

    /// <summary>
    /// The Monday-start week containing today, each day flagged if any
    /// tracked (Report/Quiz/Test) assessment is due that day - a small real
    /// signal ahead of the full Calendar page, which doesn't exist yet.
    /// </summary>
    private static List<DashboardWeekDay> BuildWeekDays(List<Assessment> trackedAssessments)
    {
        var today = DateTime.UtcNow.Date;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysSinceMonday);

        var dueDates = trackedAssessments
            .Where(a => a.DueDate.HasValue)
            .Select(a => a.DueDate!.Value.Date)
            .ToHashSet();

        return Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = monday.AddDays(offset);
                return new DashboardWeekDay(date, date == today, dueDates.Contains(date));
            })
            .ToList();
    }
}

/// <summary>One day cell in the Dashboard's "This week" preview.</summary>
public sealed record DashboardWeekDay(DateTime Date, bool IsToday, bool HasAssessmentDue);
