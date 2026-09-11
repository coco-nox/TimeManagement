using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Tutor;

/// <summary>
/// The Tutor page's three tabs - Chat, Report, and Quiz - all in one page
/// model since they share a course-picking sidebar and an "assessments
/// tracked" tally. Each tab has its own live view plus an archived-sessions
/// view, following the same active/archived pattern for all three (see
/// GetActiveConversationIdAsync, GetActiveChecklistSessionIdAsync and
/// GetActiveQuizSessionIdAsync).
/// </summary>
public class IndexModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    TutorChatService tutorChatService,
    QuizGenerationService quizGenerationService) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly TutorChatService _tutorChatService = tutorChatService;
    private readonly QuizGenerationService _quizGenerationService = quizGenerationService;

    private static readonly string[] KnownTabs = ["chat", "report", "quiz"];

    public List<Course> Courses { get; set; } = [];

    public int? SelectedCourseId { get; set; }

    public Course? SelectedCourse { get; set; }

    /// <summary>Which of the three tabs is showing: "chat", "report", or "quiz".</summary>
    public string ActiveTab { get; set; } = "chat";

    /// <summary>True when the page is showing archived sessions for the
    /// active tab instead of its live view.</summary>
    public bool ShowArchive { get; set; }

    /// <summary>Total assessments across every one of this user's courses,
    /// shown as the sidebar's headline stat.</summary>
    public int TotalAssessmentsTracked { get; set; }

    // ---- Chat tab state -------------------------------------------------

    public List<ChatMessage> ChatHistory { get; set; } = [];

    public List<ArchivedConversationSummary> ArchivedConversations { get; set; } = [];

    public Guid? ViewingConversationId { get; set; }

    public List<ChatMessage> ViewingConversationMessages { get; set; } = [];

    // ---- Report tab state -------------------------------------------------

    /// <summary>The course's Report-category assessments, offered as the
    /// picker for which assessment's checklist to view.</summary>
    public List<Assessment> ReportAssessments { get; set; } = [];

    public int? SelectedAssessmentId { get; set; }

    public Assessment? SelectedAssessment { get; set; }

    public List<AssessmentChecklistItem> ChecklistItems { get; set; } = [];

    public List<ArchivedChecklistSessionSummary> ArchivedChecklistSessions { get; set; } = [];

    public Guid? ViewingChecklistSessionId { get; set; }

    public List<AssessmentChecklistItem> ViewingChecklistItems { get; set; } = [];

    // ---- Quiz tab state -------------------------------------------------

    /// <summary>The course's Quiz-category assessments, offered as the
    /// picker for which assessment to generate quiz questions from.</summary>
    public List<Assessment> QuizAssessments { get; set; } = [];

    public List<QuizTopicStat> QuizStats { get; set; } = [];

    public List<ArchivedQuizSessionSummary> ArchivedQuizSessions { get; set; } = [];

    public Guid? ViewingQuizSessionId { get; set; }

    public List<QuizAttempt> ViewingQuizAttempts { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(
        int? courseId,
        string tab = "chat",
        int? assessmentId = null,
        bool archive = false,
        Guid? conversationId = null,
        Guid? sessionId = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        ActiveTab = KnownTabs.Contains(tab) ? tab : "chat";

        // Assessments/Documents are included here (not just when a course is
        // selected) because the sidebar shows a per-course document count.
        Courses = await _db.Courses
            .Include(c => c.Assessments)
                .ThenInclude(a => a.Documents)
            .Where(c => c.UserId == user.Id)
            .OrderBy(c => c.Title)
            .ToListAsync();

        // Only Report/Quiz/Test count as "tracked" - Coursework doesn't have
        // the checklist/quiz/report machinery the tracker exists to reflect.
        TotalAssessmentsTracked = Courses.Sum(c => c.Assessments.Count(a => a.Category != AssessmentCategory.Coursework));

        if (!courseId.HasValue)
        {
            return Page();
        }

        // Confirms the course belongs to this user before showing any of its
        // tab content - same ownership check used everywhere else in the app.
        var course = Courses.FirstOrDefault(c => c.Id == courseId.Value);
        if (course == null)
        {
            return NotFound();
        }

        SelectedCourseId = course.Id;
        SelectedCourse = course;
        ShowArchive = archive;

        switch (ActiveTab)
        {
            case "report":
                return await LoadReportTabAsync(course, assessmentId, sessionId);
            case "quiz":
                return await LoadQuizTabAsync(course, assessmentId, sessionId);
            default:
                await LoadChatTabAsync(user.Id, course, conversationId);
                return Page();
        }
    }

    private async Task LoadChatTabAsync(string userId, Course course, Guid? conversationId)
    {
        if (ShowArchive)
        {
            if (conversationId.HasValue)
            {
                ViewingConversationId = conversationId;
                ViewingConversationMessages = await _db.ChatMessages
                    .Where(m => m.UserId == userId && m.CourseId == course.Id
                        && m.ConversationId == conversationId.Value && m.ArchivedUtc != null)
                    .OrderBy(m => m.SentUtc)
                    .ToListAsync();
            }
            else
            {
                // Grouped in memory rather than in the database query -
                // simpler than translating "first user message per group"
                // into SQL, and a course's archived history is never large
                // enough for that to matter.
                var archivedMessages = await _db.ChatMessages
                    .Where(m => m.UserId == userId && m.CourseId == course.Id && m.ArchivedUtc != null)
                    .OrderBy(m => m.SentUtc)
                    .ToListAsync();

                ArchivedConversations = archivedMessages
                    .GroupBy(m => m.ConversationId)
                    .Select(g => new ArchivedConversationSummary(
                        g.Key,
                        g.Min(m => m.SentUtc),
                        g.Max(m => m.ArchivedUtc!.Value),
                        g.Count(),
                        g.FirstOrDefault(m => m.Role == "user")?.Content))
                    .OrderByDescending(s => s.ArchivedUtc)
                    .ToList();
            }
        }
        else
        {
            // Only the active (unarchived) conversation shows in the chat
            // window - archiving is what keeps this list from growing
            // forever without deleting the older history outright.
            ChatHistory = await _db.ChatMessages
                .Where(m => m.UserId == userId && m.CourseId == course.Id && m.ArchivedUtc == null)
                .OrderBy(m => m.SentUtc)
                .ToListAsync();
        }
    }

    private async Task<IActionResult> LoadReportTabAsync(Course course, int? assessmentId, Guid? sessionId)
    {
        ReportAssessments = course.Assessments
            .Where(a => a.Category == AssessmentCategory.Report)
            .OrderBy(a => a.Title)
            .ToList();

        if (!assessmentId.HasValue)
        {
            return Page();
        }

        var assessment = ReportAssessments.FirstOrDefault(a => a.Id == assessmentId.Value);
        if (assessment == null)
        {
            return NotFound();
        }

        SelectedAssessmentId = assessment.Id;
        SelectedAssessment = assessment;

        if (ShowArchive)
        {
            if (sessionId.HasValue)
            {
                ViewingChecklistSessionId = sessionId;
                ViewingChecklistItems = await _db.AssessmentChecklistItems
                    .Where(i => i.AssessmentId == assessment.Id && i.SessionId == sessionId.Value && i.ArchivedUtc != null)
                    .OrderBy(i => i.Id)
                    .ToListAsync();
            }
            else
            {
                var archivedItems = await _db.AssessmentChecklistItems
                    .Where(i => i.AssessmentId == assessment.Id && i.ArchivedUtc != null)
                    .ToListAsync();

                ArchivedChecklistSessions = archivedItems
                    .GroupBy(i => i.SessionId)
                    .Select(g => new ArchivedChecklistSessionSummary(
                        g.Key,
                        g.Max(i => i.ArchivedUtc!.Value),
                        g.Count(i => i.IsCompleted),
                        g.Count()))
                    .OrderByDescending(s => s.ArchivedUtc)
                    .ToList();
            }
        }
        else
        {
            ChecklistItems = await _db.AssessmentChecklistItems
                .Where(i => i.AssessmentId == assessment.Id && i.ArchivedUtc == null)
                .OrderBy(i => i.Id)
                .ToListAsync();

            // Defensive fallback: an assessment recategorized to Report after
            // creation (see Courses/Index.cshtml.cs OnPostUpdateAssessmentCategoryAsync)
            // never had a checklist seeded for it. Seeding lazily here, rather
            // than only at creation time, means the Report tab is never a
            // dead end for it.
            if (ChecklistItems.Count == 0)
            {
                var seeded = AssessmentChecklistItem.CreateDefaultSet(assessment.Id);
                _db.AssessmentChecklistItems.AddRange(seeded);
                await _db.SaveChangesAsync();
                ChecklistItems = seeded;
            }
        }

        return Page();
    }

    private async Task<IActionResult> LoadQuizTabAsync(Course course, int? assessmentId, Guid? sessionId)
    {
        QuizAssessments = course.Assessments
            .Where(a => a.Category == AssessmentCategory.Quiz)
            .OrderBy(a => a.Title)
            .ToList();

        if (!assessmentId.HasValue)
        {
            return Page();
        }

        var assessment = QuizAssessments.FirstOrDefault(a => a.Id == assessmentId.Value);
        if (assessment == null)
        {
            return NotFound();
        }

        SelectedAssessmentId = assessment.Id;
        SelectedAssessment = assessment;

        if (ShowArchive)
        {
            if (sessionId.HasValue)
            {
                ViewingQuizSessionId = sessionId;
                ViewingQuizAttempts = await _db.QuizAttempts
                    .Where(a => a.AssessmentId == assessment.Id && a.SessionId == sessionId.Value && a.ArchivedUtc != null)
                    .OrderBy(a => a.AttemptedUtc)
                    .ToListAsync();
            }
            else
            {
                var archivedAttempts = await _db.QuizAttempts
                    .Where(a => a.AssessmentId == assessment.Id && a.ArchivedUtc != null)
                    .ToListAsync();

                ArchivedQuizSessions = archivedAttempts
                    .GroupBy(a => a.SessionId)
                    .Select(g => new ArchivedQuizSessionSummary(
                        g.Key,
                        g.Max(a => a.ArchivedUtc!.Value),
                        g.Count(a => a.IsCorrect),
                        g.Count()))
                    .OrderByDescending(s => s.ArchivedUtc)
                    .ToList();
            }
        }
        else
        {
            var activeAttempts = await _db.QuizAttempts
                .Where(a => a.AssessmentId == assessment.Id && a.ArchivedUtc == null)
                .ToListAsync();

            QuizStats = BuildQuizStats(activeAttempts);
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

        return RedirectToPage(new { courseId, tab = "chat" });
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

        return RedirectToPage(new { courseId, tab = "chat" });
    }

    /// <summary>
    /// Toggles one checklist item's completion via the Report tab's
    /// checkbox, without reloading the page, then recomputes whether the
    /// owning assessment counts as complete. itemId can safely be
    /// [FromForm] here (unlike the archive/delete handlers above) because
    /// this is a real fetch() POST body, not a courseId threaded through
    /// asp-route-* on a plain &lt;form&gt; submit.
    /// </summary>
    public async Task<IActionResult> OnPostToggleChecklistItemAsync([FromForm] int itemId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var item = await _db.AssessmentChecklistItems
            .Include(i => i.Assessment)
                .ThenInclude(a => a!.Course)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.ArchivedUtc == null);

        if (item?.Assessment?.Course == null || item.Assessment.Course.UserId != user.Id)
        {
            return NotFound();
        }

        item.IsCompleted = !item.IsCompleted;
        await _db.SaveChangesAsync();

        var sessionItems = await _db.AssessmentChecklistItems
            .Where(i => i.AssessmentId == item.AssessmentId && i.SessionId == item.SessionId && i.ArchivedUtc == null)
            .ToListAsync();

        var completedCount = sessionItems.Count(i => i.IsCompleted);
        var totalCount = sessionItems.Count;
        var allComplete = totalCount > 0 && completedCount == totalCount;

        var assessment = item.Assessment;
        if (allComplete && !assessment.IsCompleted)
        {
            assessment.IsCompleted = true;
            assessment.CompletedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        else if (!allComplete && assessment.IsCompleted)
        {
            // Unchecking an item after finishing the checklist reopens the
            // assessment rather than leaving a stale "complete" state.
            assessment.IsCompleted = false;
            assessment.CompletedUtc = null;
            await _db.SaveChangesAsync();
        }

        return new JsonResult(new
        {
            itemId = item.Id,
            isCompleted = item.IsCompleted,
            completedCount,
            totalCount,
            assessmentIsCompleted = assessment.IsCompleted
        });
    }

    /// <summary>
    /// Archives the assessment's current checklist session and immediately
    /// seeds a fresh default set under a new SessionId - the Report tab's
    /// equivalent of <see cref="OnPostArchiveConversationAsync"/>. Same
    /// asp-route-* reasoning applies to both courseId and assessmentId here.
    /// </summary>
    public async Task<IActionResult> OnPostArchiveChecklistAsync(int courseId, int assessmentId)
    {
        var assessment = await LoadOwnedAssessmentAsync(courseId, assessmentId);
        if (assessment == null)
        {
            return NotFound();
        }

        var activeItems = await _db.AssessmentChecklistItems
            .Where(i => i.AssessmentId == assessmentId && i.ArchivedUtc == null)
            .ToListAsync();

        var archivedUtc = DateTime.UtcNow;
        foreach (var item in activeItems)
        {
            item.ArchivedUtc = archivedUtc;
        }

        _db.AssessmentChecklistItems.AddRange(AssessmentChecklistItem.CreateDefaultSet(assessmentId));

        // The old session is gone and the new one starts unchecked, so the
        // assessment can't still be marked complete.
        assessment.IsCompleted = false;
        assessment.CompletedUtc = null;

        await _db.SaveChangesAsync();

        return RedirectToPage(new { courseId, tab = "report", assessmentId });
    }

    /// <summary>
    /// Archives the assessment's current quiz session. Unlike the Report
    /// tab, nothing is re-seeded here - there's nothing to seed, since quiz
    /// questions are generated on demand rather than stored. The next
    /// answered question simply starts a new SessionId (see
    /// <see cref="GetActiveQuizSessionIdAsync"/>). Same asp-route-* reasoning
    /// as <see cref="OnPostArchiveChecklistAsync"/>.
    /// </summary>
    public async Task<IActionResult> OnPostArchiveQuizAsync(int courseId, int assessmentId)
    {
        var assessment = await LoadOwnedAssessmentAsync(courseId, assessmentId);
        if (assessment == null)
        {
            return NotFound();
        }

        var activeAttempts = await _db.QuizAttempts
            .Where(a => a.AssessmentId == assessmentId && a.ArchivedUtc == null)
            .ToListAsync();

        var archivedUtc = DateTime.UtcNow;
        foreach (var attempt in activeAttempts)
        {
            attempt.ArchivedUtc = archivedUtc;
        }

        await _db.SaveChangesAsync();

        return RedirectToPage(new { courseId, tab = "quiz", assessmentId });
    }

    /// <summary>
    /// Called by the Quiz tab's fetch() to generate one fresh batch of
    /// questions from the selected assessment's document text. Questions are
    /// never persisted - only answers are (see <see cref="OnPostAnswerQuizAsync"/>).
    /// </summary>
    public async Task<IActionResult> OnPostGenerateQuizAsync([FromForm] int courseId, [FromForm] int assessmentId)
    {
        var assessment = await LoadOwnedAssessmentAsync(courseId, assessmentId);
        if (assessment == null)
        {
            return NotFound();
        }

        var sourceDocuments = assessment.Documents
            .Select(d => new TutorSourceDocument(d.OriginalFileName, d.ExtractedText))
            .ToList();

        var result = await _quizGenerationService.GenerateAsync(sourceDocuments);

        return new JsonResult(new
        {
            questions = result.Questions.Select(q => new
            {
                question = q.Question,
                options = q.Options,
                correctAnswer = q.CorrectAnswer,
                topic = q.Topic
            }),
            error = result.Error
        });
    }

    /// <summary>
    /// Records one answered quiz question as a QuizAttempt and returns the
    /// assessment's updated per-topic accuracy, so the Quiz tab's fetch()
    /// can refresh the stats panel without reloading the page.
    /// </summary>
    public async Task<IActionResult> OnPostAnswerQuizAsync(
        [FromForm] int courseId,
        [FromForm] int assessmentId,
        [FromForm] string topic,
        [FromForm] bool isCorrect)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return BadRequest("Missing topic.");
        }

        var assessment = await LoadOwnedAssessmentAsync(courseId, assessmentId);
        if (assessment == null)
        {
            return NotFound();
        }

        var sessionId = await GetActiveQuizSessionIdAsync(assessmentId);

        _db.QuizAttempts.Add(new QuizAttempt
        {
            AssessmentId = assessmentId,
            Topic = topic.Trim(),
            IsCorrect = isCorrect,
            AttemptedUtc = DateTime.UtcNow,
            SessionId = sessionId
        });
        await _db.SaveChangesAsync();

        var activeAttempts = await _db.QuizAttempts
            .Where(a => a.AssessmentId == assessmentId && a.ArchivedUtc == null)
            .ToListAsync();

        var stats = BuildQuizStats(activeAttempts);

        return new JsonResult(new
        {
            stats = stats.Select(s => new
            {
                topic = s.Topic,
                correct = s.Correct,
                total = s.Total,
                accuracyPercent = s.AccuracyPercent,
                struggled = s.Struggled
            })
        });
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

    /// <summary>
    /// The quiz session a newly-answered question should join - the same
    /// "most recent unarchived row's session, or a brand new id" pattern as
    /// <see cref="GetActiveConversationIdAsync"/>.
    /// </summary>
    private async Task<Guid> GetActiveQuizSessionIdAsync(int assessmentId)
    {
        var activeSessionId = await _db.QuizAttempts
            .Where(a => a.AssessmentId == assessmentId && a.ArchivedUtc == null)
            .OrderByDescending(a => a.AttemptedUtc)
            .Select(a => a.SessionId)
            .FirstOrDefaultAsync();

        return activeSessionId == Guid.Empty ? Guid.NewGuid() : activeSessionId;
    }

    /// <summary>
    /// Loads an assessment by id, but only if it (via its course) belongs to
    /// the signed-in user - the Report/Quiz POST handlers' equivalent of
    /// Pages/Courses/Index.cshtml.cs's LoadOwnedCourseAsync.
    /// </summary>
    private async Task<Assessment?> LoadOwnedAssessmentAsync(int courseId, int assessmentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return null;
        }

        return await _db.Assessments
            .Include(a => a.Documents)
            .Include(a => a.Course)
            .FirstOrDefaultAsync(a => a.Id == assessmentId && a.CourseId == courseId && a.Course != null && a.Course.UserId == user.Id);
    }

    private static List<QuizTopicStat> BuildQuizStats(List<QuizAttempt> attempts)
    {
        return attempts
            .GroupBy(a => a.Topic)
            .Select(g =>
            {
                var correct = g.Count(a => a.IsCorrect);
                var total = g.Count();
                var wrong = total - correct;
                return new QuizTopicStat(
                    g.Key,
                    correct,
                    total,
                    total > 0 ? (int)Math.Round(correct * 100.0 / total) : 0,
                    wrong >= 2);
            })
            .OrderBy(s => s.Topic)
            .ToList();
    }
}

/// <summary>One row in the Chat tab's "Archived" list: enough to identify a
/// past conversation without loading its full transcript.</summary>
public sealed record ArchivedConversationSummary(
    Guid ConversationId,
    DateTime StartedUtc,
    DateTime ArchivedUtc,
    int MessageCount,
    string? FirstQuestion);

/// <summary>One row in the Report tab's "Archived" list: enough to identify
/// a past checklist session without loading every item.</summary>
public sealed record ArchivedChecklistSessionSummary(
    Guid SessionId,
    DateTime ArchivedUtc,
    int CompletedCount,
    int TotalCount)
{
    public int PercentComplete => TotalCount > 0 ? (int)Math.Round(CompletedCount * 100.0 / TotalCount) : 0;
}

/// <summary>One row in the Quiz tab's "Archived" list: enough to identify a
/// past quiz session without loading every attempt.</summary>
public sealed record ArchivedQuizSessionSummary(
    Guid SessionId,
    DateTime ArchivedUtc,
    int CorrectCount,
    int TotalCount)
{
    public int ScorePercent => TotalCount > 0 ? (int)Math.Round(CorrectCount * 100.0 / TotalCount) : 0;
}

/// <summary>One topic's accuracy within the Quiz tab's active session.
/// Struggled is true once 2 or more wrong answers have been recorded for
/// this topic, per the spec's "struggled with" tag.</summary>
public sealed record QuizTopicStat(string Topic, int Correct, int Total, int AccuracyPercent, bool Struggled);
