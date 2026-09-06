namespace TimeManagement.Models;

/// <summary>
/// One message in a user's Tutor chat history for a course - either the
/// question they typed or the AI's reply to it.
/// </summary>
public class ChatMessage
{
    public int Id { get; set; }

    /// <summary>The owning user. Every query must filter on this so a user
    /// can never see another user's chat history.</summary>
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public int CourseId { get; set; }

    public Course? Course { get; set; }

    /// <summary>Either "user" or "assistant".</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>The document the answer was drawn from, shown under an
    /// assistant reply. Null for user messages and for assistant replies
    /// that found nothing relevant.</summary>
    public string? SourceDocument { get; set; }

    public DateTime SentUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Groups messages into one chat thread for a course. All messages sent
    /// between "start" and the next archive/restart share the same id - see
    /// TutorChatService's caller (Pages/Tutor/Index.cshtml.cs) for how the
    /// active conversation is found and how a new one gets started.
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// Null while this message's conversation is the active one shown on
    /// the Tutor page. Set (to when the archive happened) once the student
    /// archives the conversation, at which point it stops showing in the
    /// active chat window but its row is kept rather than deleted.
    /// </summary>
    public DateTime? ArchivedUtc { get; set; }
}
