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
    DocumentCategorizationService categorizationService,
    ReportChecklistGenerationService reportChecklistService) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly IWebHostEnvironment _environment = environment;
    private readonly DocumentCategorizationService _categorizationService = categorizationService;
    private readonly ReportChecklistGenerationService _reportChecklistService = reportChecklistService;

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

    /// <summary>The category the student explicitly picked, if any. Null
    /// means "Auto-detect" (the default) - see OnPostUploadAsync, which
    /// falls back to DocumentCategorizationService's suggestion when this
    /// is null rather than requiring the student to know each file's type
    /// up front.</summary>
    [BindProperty]
    public AssessmentCategory? UploadCategory { get; set; }

    private static readonly string[] KnownTabs = ["upload", "preferences", "completed"];

    public List<Course> Courses { get; set; } = [];

    public int? SelectedCourseId { get; set; }

    public Course? SelectedCourse { get; set; }

    /// <summary>Which of the two in-page tabs is showing: "upload" or "preferences".</summary>
    public string ActiveTab { get; set; } = "upload";

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

    public async Task<IActionResult> OnGetAsync(int? courseId, string tab = "upload")
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        ActiveTab = KnownTabs.Contains(tab) ? tab : "upload";

        Courses = await LoadCoursesAsync(user.Id);
        // Only Report/Quiz/Test count as "tracked" - Coursework doesn't have
        // the checklist/quiz/report machinery the tracker exists to reflect.
        TotalAssessmentsTracked = Courses.Sum(c => c.Assessments.Count(a => a.Category != AssessmentCategory.Coursework));

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
            // Only Report/Quiz/Test count as "tracked" - Coursework doesn't have
            // the checklist/quiz/report machinery the tracker exists to reflect.
            TotalAssessmentsTracked = Courses.Sum(c => c.Assessments.Count(a => a.Category != AssessmentCategory.Coursework));
            return Page();
        }

        // Rotates through the standard folder colours by how many courses
        // this user already has, so a new course doesn't default to the
        // same colour as an existing one until the set wraps around.
        var existingCourseCount = await _db.Courses.CountAsync(c => c.UserId == user.Id);
        var colour = FolderColours.All[existingCourseCount % FolderColours.All.Count].Hex;

        var course = new Course
        {
            UserId = user.Id,
            Title = Input.Title.Trim(),
            CreatedUtc = DateTime.UtcNow,
            ColourHex = colour
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
            return RedirectToPage(new { courseId, tab = "upload" });
        }

        var extension = Path.GetExtension(UploadedFile.FileName).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var contentType))
        {
            ErrorMessage = "Only PDF and Word (.docx) files are supported.";
            return RedirectToPage(new { courseId, tab = "upload" });
        }

        if (UploadedFile.Length > MaxFileSizeBytes)
        {
            ErrorMessage = "That file is too large. The maximum size is 20 MB.";
            return RedirectToPage(new { courseId, tab = "upload" });
        }

        // Save under a random, GUID-based filename rather than the name the
        // user uploaded. That sidesteps two problems at once: two uploads
        // can't collide on the same filename, and we never have to trust
        // characters from user input inside a file system path.
        var storedFileName = $"{Guid.NewGuid()}{extension}";

        // Saved into the course's root folder first, not straight into a
        // category subfolder - when UploadCategory is null (Auto-detect,
        // the default), the category itself isn't known until after the
        // text below is extracted and categorised, so there's nowhere
        // final to put it yet. It's moved into the right subfolder once
        // finalCategory is settled below.
        var courseFolder = GetCourseFolder(course.Id);
        Directory.CreateDirectory(courseFolder);
        var stagingPath = Path.Combine(courseFolder, storedFileName);

        // Scoped explicitly (not a using-declaration) so the write stream -
        // opened exclusively, since FileMode.Create defaults to FileShare.None -
        // is closed before TryExtractText below tries to open the same file
        // to read it back. A using-declaration here would keep it open (and
        // locked) until the end of this method, making every extraction
        // attempt fail with a file-in-use error that TryExtractText's
        // catch-all silently swallows into "no text available".
        await using (var fileStream = new FileStream(stagingPath, FileMode.Create))
        {
            await UploadedFile.CopyToAsync(fileStream);
        }

        // Best-effort text extraction: a failure here still leaves a
        // perfectly good upload, just with no extracted text.
        var extractedText = TextExtractionService.TryExtractText(stagingPath, UploadedFile.FileName);

        var assessmentTitle = Path.GetFileNameWithoutExtension(UploadedFile.FileName);
        if (string.IsNullOrWhiteSpace(assessmentTitle))
        {
            assessmentTitle = "Untitled assessment";
        }

        var categorisation = await _categorizationService.CategorizeAsync(extractedText ?? string.Empty);

        // Whatever the student explicitly picked wins; otherwise trust the
        // detector - this is what lets someone upload a batch of documents
        // without having to know each one's exact type up front.
        var wasCategoryChosenManually = UploadCategory.HasValue;
        var finalCategory = UploadCategory ?? categorisation.Category;

        var categoryFolder = GetCategoryFolder(course.Id, finalCategory);
        Directory.CreateDirectory(categoryFolder);
        var filePath = Path.Combine(categoryFolder, storedFileName);
        System.IO.File.Move(stagingPath, filePath);

        var assessment = new Assessment
        {
            CourseId = course.Id,
            Title = assessmentTitle,
            Category = finalCategory,
            DueDate = categorisation.DueDate,
            DueDateConfirmed = categorisation.DueDate != null,
            CreatedUtc = DateTime.UtcNow
        };

        _db.Assessments.Add(assessment);
        await _db.SaveChangesAsync();

        // Report assessments get a checklist as soon as they exist, so
        // there's something to show the first time a student opens the
        // Report tab for it - tailored to this document's own rubric/brief
        // where the AI can read one, falling back to standard sections
        // otherwise (see ReportChecklistGenerationService).
        if (assessment.Category == AssessmentCategory.Report)
        {
            var checklistItems = await _reportChecklistService.BuildChecklistAsync(
                assessment.Id,
                course.Title,
                assessment.Title,
                [new TutorSourceDocument(UploadedFile.FileName, extractedText)]);
            _db.AssessmentChecklistItems.AddRange(checklistItems);
            await _db.SaveChangesAsync();
        }

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

        StatusMessage = wasCategoryChosenManually
            ? $"\"{document.OriginalFileName}\" was uploaded and assigned to \"{assessment.Title}\" ({finalCategory})."
            : $"\"{document.OriginalFileName}\" was uploaded and auto-categorised as {finalCategory} (\"{assessment.Title}\").";
        return RedirectToPage(new { courseId, tab = "upload" });
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
        return RedirectToPage(new { courseId, tab = "preferences" });
    }

    /// <summary>
    /// Sets (or clears, if the date field was left blank) an assessment's
    /// due date by hand - the Preferences tab's escape hatch for whenever
    /// the uploaded document's text didn't contain one the AI could detect
    /// at upload time (see OnPostUploadAsync's categorisation.DueDate).
    /// Also lets a wrong AI-detected date be corrected, not just a missing
    /// one filled in - same single control either way. Manually set here
    /// counts as "confirmed" exactly like an AI-detected one did.
    /// </summary>
    public async Task<IActionResult> OnPostUpdateAssessmentDueDateAsync(int courseId, int assessmentId, DateTime? dueDate)
    {
        var course = await LoadOwnedCourseAsync(courseId);
        if (course == null)
        {
            return NotFound();
        }

        var assessment = course.Assessments.FirstOrDefault(a => a.Id == assessmentId);
        if (assessment == null)
        {
            return NotFound();
        }

        assessment.DueDate = dueDate;
        assessment.DueDateConfirmed = dueDate.HasValue;
        await _db.SaveChangesAsync();

        StatusMessage = dueDate.HasValue
            ? $"Due date for \"{assessment.Title}\" set to {dueDate.Value:d MMM yyyy}."
            : $"Due date for \"{assessment.Title}\" was cleared.";

        return RedirectToPage(new { courseId, tab = "preferences" });
    }

    /// <summary>
    /// Manually flips an assessment's IsCompleted, the only completion path
    /// Quiz/Test assessments have at all (Report gets one automatically from
    /// its checklist - see Pages/Tutor/Index.cshtml.cs OnPostToggleChecklistItemAsync -
    /// but nothing does the equivalent for the other two categories). Offered
    /// any time for a non-Coursework assessment, not just once overdue - a
    /// student who finishes early shouldn't have to wait for the due date to
    /// pass before it counts - and reversible, the same "manual override,
    /// easy to undo" shape as the rest of the app rather than a one-way
    /// action. Completing an
    /// assessment moves it off the Upload tab's list and into the Completed
    /// tab (see the Upload/Completed tabs' own Assessments queries in
    /// Index.cshtml); returnTab is which of those two this was clicked from,
    /// so undoing it from the Completed tab lands back there instead of
    /// bouncing to Upload.
    /// </summary>
    public async Task<IActionResult> OnPostToggleAssessmentCompleteAsync(int courseId, int assessmentId, string returnTab = "upload")
    {
        var course = await LoadOwnedCourseAsync(courseId);
        if (course == null)
        {
            return NotFound();
        }

        var assessment = course.Assessments.FirstOrDefault(a => a.Id == assessmentId);
        if (assessment == null)
        {
            return NotFound();
        }

        assessment.IsCompleted = !assessment.IsCompleted;
        assessment.CompletedUtc = assessment.IsCompleted ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();

        StatusMessage = assessment.IsCompleted
            ? $"\"{assessment.Title}\" was marked complete."
            : $"\"{assessment.Title}\" was marked incomplete.";

        var landingTab = KnownTabs.Contains(returnTab) ? returnTab : "upload";
        return RedirectToPage(new { courseId, tab = landingTab });
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
        return RedirectToPage(new { courseId, tab = "upload" });
    }

    /// <summary>
    /// Deletes an assessment entirely - whatever its category (Coursework,
    /// Quiz, Report, Test) - along with every document attached to it, both
    /// the database rows and the files on disk. Unlike
    /// <see cref="OnPostDeleteDocumentAsync"/>, which only removes one file
    /// at a time, this removes the assessment itself.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAssessmentAsync(int courseId, int assessmentId)
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

        // The Documents rows disappear on their own via the cascade delete
        // configured in ApplicationDbContext, but that only removes database
        // rows - the files on disk need deleting explicitly, same as
        // OnPostDeleteDocumentAsync does for a single document.
        var categoryFolder = GetCategoryFolder(course.Id, assessment.Category);
        foreach (var document in assessment.Documents)
        {
            var filePath = Path.Combine(categoryFolder, document.StoredFileName);
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
        }

        _db.Assessments.Remove(assessment);
        await _db.SaveChangesAsync();

        StatusMessage = $"\"{assessment.Title}\" was deleted.";
        return RedirectToPage(new { courseId, tab = "preferences" });
    }

    /// <summary>
    /// Deletes an entire course - every assessment, document, chat message,
    /// checklist item and quiz attempt that belongs to it, both the database
    /// rows (cascade-deleted via the FK configuration in
    /// ApplicationDbContext once the Course row is removed) and every file
    /// on disk, in one go. Unlike <see cref="OnPostDeleteAssessmentAsync"/>,
    /// this doesn't need to walk each assessment's own category folder -
    /// every one of a course's files lives somewhere under its single
    /// course-level folder (see GetCourseFolder), so removing that whole
    /// folder tree covers all of them at once.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteCourseAsync(int courseId)
    {
        var course = await LoadOwnedCourseAsync(courseId);
        if (course == null)
        {
            return NotFound();
        }

        var courseFolder = GetCourseFolder(course.Id);
        if (Directory.Exists(courseFolder))
        {
            Directory.Delete(courseFolder, recursive: true);
        }

        _db.Courses.Remove(course);
        await _db.SaveChangesAsync();

        StatusMessage = $"\"{course.Title}\" and everything in it was deleted.";

        // The course no longer exists, so there's nothing left to select.
        return RedirectToPage();
    }

    /// <summary>
    /// A real, simple stand-in for progress until actual checklists (Report)
    /// and quiz results (Quiz/Test) exist: how much of the time between an
    /// assessment being added and its due date has elapsed. Null percent
    /// means there's nothing to show a bar for (no due date yet). Checked
    /// first, ahead of the due-date math below, so a completed assessment
    /// always reads as done instead of "Overdue" once its date has passed -
    /// see OnPostToggleAssessmentCompleteAsync, the manual completion path
    /// for assessments (Quiz/Test especially) that have no other way to
    /// ever reach IsCompleted.
    /// </summary>
    public AssessmentProgress GetProgress(Assessment assessment)
    {
        if (assessment.IsCompleted)
        {
            var completedLabel = assessment.CompletedUtc.HasValue
                ? $"Completed {assessment.CompletedUtc.Value:d MMM yyyy}"
                : "Completed";
            return new AssessmentProgress(100, completedLabel, "bg-success");
        }

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
