using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Courses;

public class DetailsModel(
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

    public Course Course { get; set; } = null!;

    [BindProperty]
    public IFormFile? UploadedFile { get; set; }

    [BindProperty]
    public AssessmentCategory UploadCategory { get; set; } = AssessmentCategory.Coursework;

    /// <summary>Shown once after a successful upload or delete.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>Shown once when an upload is rejected.</summary>
    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var course = await LoadOwnedCourseAsync(id);
        if (course == null)
        {
            return NotFound();
        }

        Course = course;
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(int id)
    {
        var course = await LoadOwnedCourseAsync(id);
        if (course == null)
        {
            return NotFound();
        }

        if (UploadedFile == null || UploadedFile.Length == 0)
        {
            ErrorMessage = "Please choose a file to upload.";
            return RedirectToPage(new { id });
        }

        var extension = Path.GetExtension(UploadedFile.FileName).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var contentType))
        {
            ErrorMessage = "Only PDF and Word (.docx) files are supported.";
            return RedirectToPage(new { id });
        }

        if (UploadedFile.Length > MaxFileSizeBytes)
        {
            ErrorMessage = "That file is too large. The maximum size is 20 MB.";
            return RedirectToPage(new { id });
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
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUpdateAssessmentCategoryAsync(int id, int assessmentId, AssessmentCategory category)
    {
        var course = await LoadOwnedCourseAsync(id);
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
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteDocumentAsync(int id, int documentId)
    {
        var course = await LoadOwnedCourseAsync(id);
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
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Loads a course by id, but only if it belongs to the signed-in user.
    /// Returning null (which callers turn into a 404) for a course that
    /// exists but belongs to someone else is what stops a user from seeing
    /// or modifying another user's courses just by guessing an id in the URL.
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
