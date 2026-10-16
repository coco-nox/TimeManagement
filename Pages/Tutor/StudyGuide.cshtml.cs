using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Tutor;

/// <summary>
/// The Tutor's Study guide tab: pick a Test or Quiz and the AI writes a
/// printable revision guide from the course's documents - that
/// assessment's own files plus the course's Coursework material (lecture
/// notes, slides). One saved guide per assessment; Regenerate replaces it.
/// Downloads as Word (.docx), or print / save as PDF via Pages/Tutor/Print.
/// </summary>
public class StudyGuideModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, StudyGuideService studyGuideService) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly StudyGuideService _studyGuideService = studyGuideService;

    public List<Course> Courses { get; set; } = [];

    public Course? SelectedCourse { get; set; }

    /// <summary>The course's Test and Quiz assessments.</summary>
    public List<Assessment> Assessments { get; set; } = [];

    public Assessment? SelectedAssessment { get; set; }

    public StudyGuide? Guide { get; set; }

    /// <summary>The saved guide, already rendered (encoded) - see StudyGuideHtml.</summary>
    public string? GuideHtml { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int? courseId, int? assessmentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        Courses = await _db.Courses.Where(c => c.UserId == user.Id).OrderBy(c => c.Title).ToListAsync();
        if (!courseId.HasValue)
        {
            return Page();
        }

        SelectedCourse = Courses.FirstOrDefault(c => c.Id == courseId.Value);
        if (SelectedCourse == null)
        {
            return NotFound();
        }

        Assessments = await _db.Assessments
            .Where(a => a.CourseId == SelectedCourse.Id && (a.Category == AssessmentCategory.Test || a.Category == AssessmentCategory.Quiz))
            .OrderBy(a => a.IsCompleted).ThenBy(a => a.DueDate == null).ThenBy(a => a.DueDate)
            .ToListAsync();

        // Default to the soonest not-yet-done test/quiz.
        SelectedAssessment = assessmentId.HasValue
            ? Assessments.FirstOrDefault(a => a.Id == assessmentId.Value)
            : Assessments.FirstOrDefault();
        if (assessmentId.HasValue && SelectedAssessment == null)
        {
            return NotFound();
        }

        if (SelectedAssessment != null)
        {
            Guide = await _db.StudyGuides.FirstOrDefaultAsync(g => g.AssessmentId == SelectedAssessment.Id && g.UserId == user.Id);
            var content = Guide == null ? null : StudyGuideService.Parse(Guide.ContentJson);
            GuideHtml = content == null ? null : StudyGuideHtml.Render(content);
        }

        return Page();
    }

    /// <summary>Writes (or rewrites) the guide for one assessment.</summary>
    public async Task<IActionResult> OnPostGenerateAsync([FromForm] int assessmentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var assessment = await _db.Assessments
            .Include(a => a.Course)
            .FirstOrDefaultAsync(a => a.Id == assessmentId && a.Course != null && a.Course.UserId == user.Id
                && (a.Category == AssessmentCategory.Test || a.Category == AssessmentCategory.Quiz));
        if (assessment == null)
        {
            return NotFound();
        }

        // The test's own files first (practice papers, topic lists), then
        // the course's teaching material.
        var documents = await _db.Documents
            .Where(d => d.Assessment != null && d.Assessment.CourseId == assessment.CourseId
                && (d.AssessmentId == assessment.Id || d.Assessment.Category == AssessmentCategory.Coursework)
                && d.ExtractedText != null && d.ExtractedText != "")
            .OrderBy(d => d.AssessmentId == assessment.Id ? 0 : 1)
            .ThenBy(d => d.OriginalFileName)
            .Select(d => new TutorSourceDocument(d.OriginalFileName, d.ExtractedText))
            .ToListAsync();

        if (documents.Count == 0)
        {
            ErrorMessage = "There are no documents with readable text in this course yet. Upload your lecture notes or the test's topic list on the Course page, then try again.";
            return RedirectToPage(new { courseId = assessment.CourseId, assessmentId });
        }

        var result = await _studyGuideService.GenerateAsync(
            assessment.Course!.Title, assessment.Title, assessment.Category.ToString(), assessment.DueDate, documents);
        if (result.Value is not { } content)
        {
            ErrorMessage = AiClient.DescribeFailure(result, "study guide writer", "study guide", "study guide");
            return RedirectToPage(new { courseId = assessment.CourseId, assessmentId });
        }

        var guide = await _db.StudyGuides.FirstOrDefaultAsync(g => g.AssessmentId == assessment.Id);
        if (guide == null)
        {
            guide = new StudyGuide { AssessmentId = assessment.Id, UserId = user.Id };
            _db.StudyGuides.Add(guide);
        }

        var sources = string.Join(", ", documents.Select(d => d.SourceName).Distinct());
        guide.ContentJson = StudyGuideService.Serialize(content);
        guide.SourceNames = sources.Length > 1000 ? sources[..997] + "..." : sources;
        guide.GeneratedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return RedirectToPage(new { courseId = assessment.CourseId, assessmentId });
    }

    public async Task<IActionResult> OnGetDocxAsync(int guideId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var guide = await _db.StudyGuides
            .Include(g => g.Assessment).ThenInclude(a => a!.Course)
            .FirstOrDefaultAsync(g => g.Id == guideId && g.UserId == user.Id);
        var content = guide == null ? null : StudyGuideService.Parse(guide.ContentJson);
        if (guide?.Assessment == null || content == null)
        {
            return NotFound();
        }

        var title = $"{guide.Assessment.Title} - study guide";
        var subtitle = $"{guide.Assessment.Course?.Title}" + (guide.Assessment.DueDate is DateTime due ? $" · {due:dddd d MMMM}" : "");
        var bytes = await DocumentHtml.ToDocxAsync(title, subtitle, StudyGuideHtml.Render(content));
        return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", DocumentHtml.FileNameFor(title, ".docx"));
    }
}
