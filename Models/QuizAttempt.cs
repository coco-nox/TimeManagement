namespace TimeManagement.Models;

/// <summary>
/// One answered question from the Quiz tab for an assessment. Rows accumulate
/// across a session (see SessionId) and are used to compute per-topic
/// accuracy - individual questions themselves are generated on demand and
/// not stored.
/// </summary>
public class QuizAttempt
{
    public int Id { get; set; }

    public int AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    public string Topic { get; set; } = string.Empty;

    public bool IsCorrect { get; set; }

    public DateTime AttemptedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Groups attempts into one quiz session for an assessment, the same way
    /// ChatMessage.ConversationId groups Tutor chat messages. Archiving stamps
    /// ArchivedUtc on the whole session; the next answered question starts a
    /// new SessionId.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>Null while this attempt's session is the active one shown on
    /// the Quiz tab. Set once the session is archived.</summary>
    public DateTime? ArchivedUtc { get; set; }
}
