namespace TimeManagement.Models;

/// <summary>
/// One section of a Report-category assessment's completion checklist (e.g.
/// "Introduction", "Testing"). Items are seeded in a default set whenever a
/// Report assessment is created or a checklist session is archived - see
/// <see cref="CreateDefaultSet"/> and Pages/Tutor/Index.cshtml.cs.
/// </summary>
public class AssessmentChecklistItem
{
    public int Id { get; set; }

    public int AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    public string Description { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    /// <summary>
    /// Groups items into one checklist session for an assessment, the same
    /// way ChatMessage.ConversationId groups Tutor chat messages. All items
    /// seeded together share one SessionId; archiving stamps ArchivedUtc on
    /// the whole session and seeds a fresh one under a new SessionId.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>Null while this item's session is the active one shown on
    /// the Report tab. Set once the session is archived.</summary>
    public DateTime? ArchivedUtc { get; set; }

    /// <summary>The section titles every new Report checklist session starts with.</summary>
    public static readonly string[] DefaultDescriptions =
    [
        "Introduction",
        "Problem domain",
        "Requirements & design",
        "Implementation",
        "Testing",
        "Reflection"
    ];

    /// <summary>Builds one fresh default checklist session for an assessment,
    /// ready to add to the database.</summary>
    public static List<AssessmentChecklistItem> CreateDefaultSet(int assessmentId)
    {
        var sessionId = Guid.NewGuid();
        return DefaultDescriptions
            .Select(description => new AssessmentChecklistItem
            {
                AssessmentId = assessmentId,
                Description = description,
                IsCompleted = false,
                SessionId = sessionId
            })
            .ToList();
    }
}
