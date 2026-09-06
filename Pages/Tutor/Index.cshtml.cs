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

    public List<Course> Courses { get; set; } = [];

    public int? SelectedCourseId { get; set; }

    public Course? SelectedCourse { get; set; }

    public List<ChatMessage> ChatHistory { get; set; } = [];

    /// <summary>Total assessments across every one of this user's courses,
    /// shown as the sidebar's headline stat.</summary>
    public int TotalAssessmentsTracked { get; set; }

    public async Task<IActionResult> OnGetAsync(int? courseId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        // Assessments/Documents are included here (not just when a course is
        // selected) because the sidebar shows a per-course document count.
        Courses = await _db.Courses
            .Include(c => c.Assessments)
                .ThenInclude(a => a.Documents)
            .Where(c => c.UserId == user.Id)
            .OrderBy(c => c.Title)
            .ToListAsync();

        TotalAssessmentsTracked = Courses.Sum(c => c.Assessments.Count);

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
            SelectedCourse = course;

            // Only the active (unarchived) conversation shows in the chat
            // window - archiving is what keeps this list from growing
            // forever without deleting the older history outright.
            ChatHistory = await _db.ChatMessages
                .Where(m => m.UserId == user.Id && m.CourseId == course.Id && m.ArchivedUtc == null)
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
        var conversationId = await GetActiveConversationIdAsync(user.Id, course.Id);

        _db.ChatMessages.Add(new ChatMessage
        {
            UserId = user.Id,
            CourseId = course.Id,
            ConversationId = conversationId,
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
            ConversationId = conversationId,
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

    /// <summary>
    /// Archives the course's current conversation (keeps the messages, just
    /// stops showing them) so the next question asked starts a brand new
    /// one - see <see cref="GetActiveConversationIdAsync"/>. courseId must
    /// NOT be [FromForm]: the button's form gets its courseId from
    /// asp-route-courseId, which puts it in the action URL's query string,
    /// not the form body - [FromForm] would silently bind 0 and archive
    /// nothing (or the wrong course, if 0 ever matched one).
    /// </summary>
    public async Task<IActionResult> OnPostArchiveConversationAsync(int courseId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var activeMessages = await _db.ChatMessages
            .Where(m => m.UserId == user.Id && m.CourseId == courseId && m.ArchivedUtc == null)
            .ToListAsync();

        var archivedUtc = DateTime.UtcNow;
        foreach (var message in activeMessages)
        {
            message.ArchivedUtc = archivedUtc;
        }

        await _db.SaveChangesAsync();

        return RedirectToPage(new { courseId });
    }

    /// <summary>
    /// Permanently deletes the course's current conversation (not any
    /// already-archived ones) and starts fresh - unlike archiving, this
    /// can't be undone. Same reason courseId isn't [FromForm] as
    /// <see cref="OnPostArchiveConversationAsync"/>.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteConversationAsync(int courseId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var activeMessages = await _db.ChatMessages
            .Where(m => m.UserId == user.Id && m.CourseId == courseId && m.ArchivedUtc == null)
            .ToListAsync();

        _db.ChatMessages.RemoveRange(activeMessages);
        await _db.SaveChangesAsync();

        return RedirectToPage(new { courseId });
    }

    /// <summary>
    /// The conversation new messages should join: whichever conversation
    /// the course's most recent unarchived message belongs to, or a brand
    /// new id if there isn't one (first message ever, or the previous
    /// conversation was just archived/deleted).
    /// </summary>
    private async Task<Guid> GetActiveConversationIdAsync(string userId, int courseId)
    {
        var activeConversationId = await _db.ChatMessages
            .Where(m => m.UserId == userId && m.CourseId == courseId && m.ArchivedUtc == null)
            .OrderByDescending(m => m.SentUtc)
            .Select(m => m.ConversationId)
            .FirstOrDefaultAsync();

        return activeConversationId == Guid.Empty ? Guid.NewGuid() : activeConversationId;
    }
}
