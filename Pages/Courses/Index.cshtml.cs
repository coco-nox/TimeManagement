using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Courses;

/// <summary>
/// One-stop course workspace: pick a course from the sidebar, then upload
/// documents and manage its assessments on the right. This merges what used
/// to be a separate list page and a per-course details page into one, the
/// same sidebar-driven pattern as the Tutor page.
/// </summary>
public class IndexModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IWebHostEnvironment environment,
    DocumentCategorizationService categorizationService) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly IWebHostEnvironment _environment = environment;
    private readonly DocumentCategorizationService _categorizationService = categorizationService;

    // The only file types we know how to store and (try to) read text
    // from. Anything else is rejected before it touches the disk.
    private static readonly Dictionary<string, string> AllowedExtensions = new()
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    };

    private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? UploadedFile { get; set; }

    [BindProperty]
    public AssessmentCategory UploadCategory { get; set; } = AssessmentCategory.Coursework;

    public List<Course> Courses { get; set; } = [];

    public int? SelectedCourseId { get; set; }

    public Course? SelectedCourse { get; set; }

    /// <summary>Total assessments across every one of this user's courses,
    /// shown as the sidebar's headline stat.</summary>
    public int TotalAssessmentsTracked { get; set; }

    /// <summary>Shown once after a successful upload, move, delete, or course creation.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>Shown once when an upload is rejected.</summary>
    [TempData]
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a course name.")]
        [StringLength(200, ErrorMessage = "Course names must be 200 characters or fewer.")]
        [Display(Name = "Course name")]
        public string Title { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(int? courseId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        Courses = await LoadCoursesAsync(user.Id);
        TotalAssessmentsTracked = Courses.Sum(c => c.Assessments.Count);

        if (courseId.HasValue)
        {
            // Confirms the course belongs to this user before showing any
            // of its assessments - same ownership check used everywhere
            // else in the app.
            var course = Courses.FirstOrDefault(c => c.Id == courseId.Value);
            if (course == null)
            {
                return NotFound();
            }

            SelectedCourseId = course.Id;
            SelectedCourse = course;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateCourseAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            Courses = await LoadCoursesAsync(user.Id);
            TotalAssessmentsTracked = Courses.Sum(c => c.Assessments.Count);
            return Page();
        }

        var course = new Course
        {
            UserId = user.Id,
            Title = Input.Title.Trim(),
            CreatedUtc = DateTime.UtcNow
        };

        _db.Courses.Add(course);
        await _db.SaveChangesAsync();

        StatusMessage = $"\"{course.Title}\" was added.";

        // Land on the new course rather than back at an empty selection.
        return RedirectToPage(new { courseId = course.Id });
    }

    public async Task<IActionResult> OnPostUploadAsync(int courseId)
    {
        var course = await LoadOwnedCourseAsync(courseId);
        if (course == null)
        {
            return NotFound();
        }

        if (UploadedFile == null || UploadedFile.Length == 0)
        {
            ErrorMessage = "Please choose a file to upload.";
            return RedirectToPage(new { courseId });
        }

        var extension = Path.GetExtension(UploadedFile.FileName).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var contentType))
        {
            ErrorMessage = "Only PDF and Word (.docx) files are supported.";
            return RedirectToPage(new { courseId });
        }

        if (UploadedFile.Length > MaxFileSizeBytes)
        {
            ErrorMessage = "That file is too large. The maximum size is 20 MB.";
            return RedirectToPage(new { courseId });
        }

        // Save under a random, GUID-based filename rather than the name the
        // user uploaded. That sidesteps two problems at once: two uploads
        // can't collide on the same filename, and we never have to trust
        // characters from user input inside a file system path.
        var storedFileName = $"{Guid.NewGuid()}{extension}";
        var categoryFolder = GetCategoryFolder(course.Id, UploadCategory);
        Directory.CreateDirectory(categoryFolder);
        var filePath = Path.Combine(categoryFolder, storedFileName);

        await using var fileStream = new FileStream(filePath, FileMode.Create);
        await UploadedFile.CopyToAsync(fileStream);

        // Best-effort text extraction: a failure here still leaves a
        // perfectly good upload, just with no extracted text.
        var extractedText = TextExtractionService.TryExtractText(filePath, UploadedFile.FileName);

        var assessmentTitle = Path.GetFileNameWithoutExtension(UploadedFile.FileName);
        if (string.IsNullOrWhiteSpace(assessmentTitle))
        {
            assessmentTitle = "Untitled assessment";
        }

        var categorisation = await _categorizationService.CategorizeAsync(extractedText ?? string.Empty);

        var assessment = new Assessment
        {
            CourseId = course.Id,
            Title = assessmentTitle,
            Category = UploadCategory,
            DueDate = categorisation.DueDate,
            DueDateConfirmed = categorisation.DueDate != null,
            CreatedUtc = DateTime.UtcNow
        };

        _db.Assessments.Add(assessment);
        await _db.SaveChangesAsync();

        var document = new Document
        {
            AssessmentId = assessment.Id,
            OriginalFileName = UploadedFile.FileName,
            StoredFileName = storedFileName,
            ContentType = contentType,
            UploadedUtc = DateTime.UtcNow,
            ExtractedText = extractedText
        };

        _db.Documents.Add(document);
        await _db.SaveChangesAsync();

        StatusMessage = $"\"{document.OriginalFileName}\" was uploaded and assigned to \"{assessment.Title}\".";
        return RedirectToPage(new { courseId });
    }

    public async Task<IActionResult> OnPostUpdateAssessmentCategoryAsync(int courseId, int assessmentId, AssessmentCategory category)
    {
        var course = await LoadOwnedCourseAsync(courseId);
        if (course == null)
        {
            return NotFound();
        }

        var assessment = await _db.Assessments
            .Include(a => a.Documents)
            .FirstOrDefaultAsync(a => a.Id == assessmentId && a.CourseId == course.Id);

        if (assessment == null)
        {
            return NotFound();
        }

        if (assessment.Category != category)
        {
            var oldFolder = GetCategoryFolder(course.Id, assessment.Category);
            var newFolder = GetCategoryFolder(course.Id, category);
            Directory.CreateDirectory(newFolder);

            foreach (var document in assessment.Documents)
            {
                var oldPath = Path.Combine(oldFolder, document.StoredFileName);
                var newPath = Path.Combine(newFolder, document.StoredFileName);
                if (System.IO.File.Exists(oldPath))
                {
                    System.IO.File.Move(oldPath, newPath, overwrite: true);
                }
            }
        }

        assessment.Category = category;
        await _db.SaveChangesAsync();

        StatusMessage = $"\"{assessment.Title}\" was moved to {category}.";
        return RedirectToPage(new { courseId });
    }

    public async Task<IActionResult> OnPostDeleteDocumentAsync(int courseId, int documentId)
    {
        var course = await LoadOwnedCourseAsync(courseId);
        if (course == null)
        {
            return NotFound();
        }

        var document = await _db.Documents
            .Include(d => d.Assessment)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.Assessment != null && d.Assessment.CourseId == course.Id);

        if (document == null)
        {
            return NotFound();
        }

        var filePath = Path.Combine(GetCategoryFolder(course.Id, document.Assessment!.Category), document.StoredFileName);
        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
        }

        _db.Documents.Remove(document);
        await _db.SaveChangesAsync();

        StatusMessage = $"\"{document.OriginalFileName}\" was deleted.";
        return RedirectToPage(new { courseId });
    }

    /// <summary>
    /// A real, simple stand-in for progress until actual checklists (Report)
    /// and quiz results (Quiz/Test) exist: how much of the time between an
    /// assessment being added and its due date has elapsed. Null percent
    /// means there's nothing to show a bar for (no due date yet).
    /// </summary>
    public AssessmentProgress GetProgress(Assessment assessment)
    {
        if (!assessment.DueDate.HasValue)
        {
            return new AssessmentProgress(
                null,
                assessment.Category == AssessmentCategory.Coursework ? "No fixed due date" : "Due date needed",
                "bg-secondary");
        }

        var now = DateTime.UtcNow;
        if (now > assessment.DueDate.Value)
        {
            return new AssessmentProgress(100, "Overdue", "bg-danger");
        }

        var totalSpan = (assessment.DueDate.Value - assessment.CreatedUtc).TotalMinutes;
        var elapsed = (now - assessment.CreatedUtc).TotalMinutes;
        var percent = totalSpan > 0 ? (int)Math.Clamp(elapsed / totalSpan * 100, 0, 100) : 100;

        var daysLeft = Math.Ceiling((assessment.DueDate.Value - now).TotalDays);
        var label = daysLeft < 1 ? "Due today" : $"Due in {daysLeft:0} day(s)";
        var barClass = percent >= 80 ? "bg-warning" : "bg-success";

        return new AssessmentProgress(percent, label, barClass);
    }

    /// <summary>
    /// Loads a course by id, but only if it belongs to the signed-in user.
    /// Returning null (which callers turn into a 404) for a course that
    /// exists but belongs to someone else is what stops a user from seeing
    /// or modifying another user's courses just by guessing an id.
    /// </summary>
    private async Task<Course?> LoadOwnedCourseAsync(int courseId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return null;
        }

        return await _db.Courses
            .Include(c => c.Assessments)
                .ThenInclude(a => a.Documents)
            .FirstOrDefaultAsync(c => c.Id == courseId && c.UserId == user.Id);
    }

    private Task<List<Course>> LoadCoursesAsync(string userId)
    {
        return _db.Courses
            .Include(c => c.Assessments)
                .ThenInclude(a => a.Documents)
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Title)
            .ToListAsync();
    }

    /// <summary>
    /// Where a course's files live on disk. Deliberately outside wwwroot:
    /// wwwroot is served as static files to anyone, signed in or not, so a
    /// file saved there would be reachable by URL without going through
    /// the ownership check in <see cref="LoadOwnedCourseAsync"/>.
    /// </summary>
    private string GetCourseFolder(int courseId)
    {
        return Path.Combine(_environment.ContentRootPath, "UploadedDocuments", courseId.ToString());
    }

    /// <summary>
    /// Where a specific assessment category's files live within a course's
    /// folder, e.g. UploadedDocuments/3/Quiz. Keeps uploads sorted on disk
    /// the same way they're grouped in the UI, instead of all landing flat
    /// in the course folder.
    /// </summary>
    private string GetCategoryFolder(int courseId, AssessmentCategory category)
    {
        return Path.Combine(GetCourseFolder(courseId), category.ToString());
    }
}

/// <summary>A due-date-driven progress reading for one assessment. Percent is
/// null when there's no due date to measure against.</summary>
public sealed record AssessmentProgress(int? Percent, string Label, string BarClass);
