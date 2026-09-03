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
}
