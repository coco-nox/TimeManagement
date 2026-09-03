using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Tutor;

/// <summary>
/// The Tutor page's Chat tab: pick a course, ask a question, and get an
/// answer grounded in that course's uploaded document text. Other Tutor
/// tabs (Quiz, Test, Report) are out of scope here.
/// </summary>
public class IndexModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    TutorChatService tutorChatService) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly TutorChatService _tutorChatService = tutorChatService;

    public List<Course> Courses { get; set; } = new();

    public int? SelectedCourseId { get; set; }

    public List<ChatMessage> ChatHistory { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int? courseId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        Courses = await _db.Courses
            .Where(c => c.UserId == user.Id)
            .OrderBy(c => c.Title)
            .ToListAsync();

        if (courseId.HasValue)
        {
            // Confirms the course belongs to this user before showing any
            // of its chat history - same ownership check used everywhere
            // else in the app.
            var course = Courses.FirstOrDefault(c => c.Id == courseId.Value);
            if (course == null)
            {
                return NotFound();
            }

            SelectedCourseId = course.Id;
            ChatHistory = await _db.ChatMessages
                .Where(m => m.UserId == user.Id && m.CourseId == course.Id)
                .OrderBy(m => m.SentUtc)
                .ToListAsync();
        }

        return Page();
    }

    /// <summary>
    /// Called by the Chat tab's fetch() so a sent message doesn't reload the
    /// page. Returns the assistant's reply as JSON for the client to append
    /// to the chat window.
    /// </summary>
    public async Task<IActionResult> OnPostAskAsync([FromForm] int courseId, [FromForm] string question)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            return BadRequest("Please enter a question.");
        }

        var course = await _db.Courses
            .Include(c => c.Assessments)
                .ThenInclude(a => a.Documents)
            .FirstOrDefaultAsync(c => c.Id == courseId && c.UserId == user.Id);

        if (course == null)
        {
            return NotFound();
        }

        var trimmedQuestion = question.Trim();

        _db.ChatMessages.Add(new ChatMessage
        {
            UserId = user.Id,
            CourseId = course.Id,
            Role = "user",
            Content = trimmedQuestion,
            SentUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Every document belonging to any of this course's assessments -
        // this is the full pool of text the AI is allowed to answer from.
        var sourceDocuments = course.Assessments
            .SelectMany(a => a.Documents)
            .Select(d => new TutorSourceDocument(d.OriginalFileName, d.ExtractedText))
            .ToList();

        var result = await _tutorChatService.AskAsync(trimmedQuestion, sourceDocuments);

        var assistantMessage = new ChatMessage
        {
            UserId = user.Id,
            CourseId = course.Id,
            Role = "assistant",
            Content = result.Answer,
            SourceDocument = result.SourceDocument,
            SentUtc = DateTime.UtcNow
        };
        _db.ChatMessages.Add(assistantMessage);
        await _db.SaveChangesAsync();

        return new JsonResult(new
        {
            answer = assistantMessage.Content,
            sourceDocument = assistantMessage.SourceDocument,
            sentUtc = assistantMessage.SentUtc
        });
    }
}
