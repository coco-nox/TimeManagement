using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Tutor;

/// <summary>
/// The Tutor's Write tab: a word processor for the student's own papers
/// (report drafts, essays, notes), saved per course and optionally per
/// assessment. A paper for a Report assessment starts from that report's AI
/// outline (its section headings) and shows the outline's word targets and
/// the Report tab's checklist beside the editor. The editor autosaves
/// through <see cref="OnPostSaveAsync"/>; every save is sanitised (see
/// <see cref="DocumentHtml"/>), since the HTML is shown back in the page.
/// Papers download as Word (.docx) or print / save as PDF via Pages/Tutor/Print.
/// </summary>
public class WriteModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, StudySessionService studySessions) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly StudySessionService _studySessions = studySessions;

    /// <summary>Generous cap on one paper's HTML (about 100k words).</summary>
    private const int MaxHtmlLength = 2_000_000;

    public List<Course> Courses { get; set; } = [];

    public Course? SelectedCourse { get; set; }

    public List<StudyDocument> Documents { get; set; } = [];

    public StudyDocument? Document { get; set; }

    /// <summary>The course's assessments, for "what is this paper for?".</summary>
    public List<Assessment> Assessments { get; set; } = [];

    /// <summary>For a paper tied to a Report: the active outline's sections
    /// (word targets) and checklist, the same ones the Report tab shows.</summary>
    public List<ReportOutlineSection> OutlineSections { get; set; } = [];

    public int? SuggestedTotalWords { get; set; }

    public List<AssessmentChecklistItem> ChecklistItems { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int? courseId, int? documentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        Courses = await _db.Courses.Where(c => c.UserId == user.Id).OrderBy(c => c.Title).ToListAsync();

        if (documentId.HasValue)
        {
            Document = await _db.StudyDocuments
                .Include(d => d.Assessment)
                .FirstOrDefaultAsync(d => d.Id == documentId.Value && d.UserId == user.Id);
            if (Document == null)
            {
                return NotFound();
            }
            courseId = Document.CourseId;
        }

        if (!courseId.HasValue)
        {
            return Page();
        }

        SelectedCourse = Courses.FirstOrDefault(c => c.Id == courseId.Value);
        if (SelectedCourse == null)
        {
            return NotFound();
        }

        Documents = await _db.StudyDocuments
            .Include(d => d.Assessment)
            .Where(d => d.UserId == user.Id && d.CourseId == SelectedCourse.Id)
            .OrderByDescending(d => d.UpdatedUtc)
            .ToListAsync();

        Assessments = await _db.Assessments
            .Where(a => a.CourseId == SelectedCourse.Id)
            .OrderBy(a => a.Title)
            .ToListAsync();

        if (Document?.Assessment is { Category: AssessmentCategory.Report } report)
        {
            ChecklistItems = await _db.AssessmentChecklistItems
                .Where(i => i.AssessmentId == report.Id && i.ArchivedUtc == null)
                .OrderBy(i => i.Id)
                .ToListAsync();

            var sessionId = ChecklistItems.FirstOrDefault()?.SessionId;
            if (sessionId.HasValue)
            {
                var outline = await _db.ReportOutlines
                    .FirstOrDefaultAsync(o => o.AssessmentId == report.Id && o.SessionId == sessionId.Value);
                OutlineSections = outline?.GetSections() ?? [];
                SuggestedTotalWords = outline?.SuggestedTotalWords;
            }
        }

        return Page();
    }

    /// <summary>Starts a new paper. One for a Report starts with that
    /// report's outline headings already in place.</summary>
    public async Task<IActionResult> OnPostCreateAsync([FromForm] int courseId, [FromForm] int? assessmentId, [FromForm] string? title)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId && c.UserId == user.Id);
        if (course == null)
        {
            return NotFound();
        }

        Assessment? assessment = null;
        if (assessmentId.HasValue)
        {
            assessment = await _db.Assessments.FirstOrDefaultAsync(a => a.Id == assessmentId.Value && a.CourseId == course.Id);
        }

        var trimmedTitle = string.IsNullOrWhiteSpace(title) ? (assessment?.Title ?? "Untitled document") : title.Trim();
        if (trimmedTitle.Length > 200)
        {
            trimmedTitle = trimmedTitle[..200];
        }

        var html = "<p><br></p>";
        if (assessment is { Category: AssessmentCategory.Report })
        {
            var sessionId = await _db.AssessmentChecklistItems
                .Where(i => i.AssessmentId == assessment.Id && i.ArchivedUtc == null)
                .Select(i => (Guid?)i.SessionId)
                .FirstOrDefaultAsync();
            var outline = sessionId.HasValue
                ? await _db.ReportOutlines.FirstOrDefaultAsync(o => o.AssessmentId == assessment.Id && o.SessionId == sessionId.Value)
                : null;
            var sections = outline?.GetSections() ?? [];
            if (sections.Count > 0)
            {
                html = string.Concat(sections.Select(s => $"<h2>{WebUtility.HtmlEncode(s.Title)}</h2><p><br></p>"));
            }
        }

        var document = new StudyDocument
        {
            UserId = user.Id,
            CourseId = course.Id,
            AssessmentId = assessment?.Id,
            Title = trimmedTitle,
            ContentHtml = DocumentHtml.Sanitize(html)
        };
        _db.StudyDocuments.Add(document);
        await _db.SaveChangesAsync();

        return RedirectToPage(new { documentId = document.Id });
    }

    /// <summary>Autosave (fetch from the editor). Returns the saved word
    /// count so the side panel stays in step with what was stored.</summary>
    public async Task<IActionResult> OnPostSaveAsync([FromForm] int documentId, [FromForm] string? title, [FromForm] string? html)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var document = await _db.StudyDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.UserId == user.Id);
        if (document == null)
        {
            return NotFound(new { error = "This paper no longer exists." });
        }

        if (html != null && html.Length > MaxHtmlLength)
        {
            return BadRequest(new { error = "This paper is too long to save in one piece." });
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var trimmed = title.Trim();
            document.Title = trimmed.Length > 200 ? trimmed[..200] : trimmed;
        }

        if (html != null)
        {
            var previousWords = document.WordCount;
            document.ContentHtml = DocumentHtml.Sanitize(html);
            document.WordCount = DocumentHtml.CountWords(document.ContentHtml);

            // Words added count towards the running study session, if any.
            var added = document.WordCount - previousWords;
            if (added > 0)
            {
                await _studySessions.RecordAsync(user.Id, s => s.WordsWritten += added);
            }
        }

        document.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return new JsonResult(new { ok = true, wordCount = document.WordCount, savedUtc = document.UpdatedUtc });
    }

    public async Task<IActionResult> OnPostDeleteAsync([FromForm] int documentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var document = await _db.StudyDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.UserId == user.Id);
        if (document == null)
        {
            return NotFound();
        }

        _db.StudyDocuments.Remove(document);
        await _db.SaveChangesAsync();
        StatusMessage = $"Deleted \"{document.Title}\".";
        return RedirectToPage(new { courseId = document.CourseId });
    }

    /// <summary>Download as a Word document.</summary>
    public async Task<IActionResult> OnGetDocxAsync(int documentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var document = await _db.StudyDocuments
            .Include(d => d.Course)
            .Include(d => d.Assessment)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.UserId == user.Id);
        if (document == null)
        {
            return NotFound();
        }

        var subtitle = document.Assessment != null ? $"{document.Course?.Title} · {document.Assessment.Title}" : document.Course?.Title;
        var bytes = await DocumentHtml.ToDocxAsync(document.Title, subtitle, document.ContentHtml);
        return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", DocumentHtml.FileNameFor(document.Title, ".docx"));
    }
}
