namespace TimeManagement.Models;

/// <summary>
/// One section of a Report-category assessment's completion checklist.
/// Normally generated from the assessment's own uploaded rubric/brief and
/// the course it belongs to (see Services/ReportChecklistGenerationService.cs),
/// so an item usually names both a section and its specific requirement
/// (e.g. "Testing: include unit tests and a documented test plan (20%)")
/// rather than a bare label. <see cref="CreateDefaultSet"/> is the fallback
/// used when there's nothing to generate from - no AI configured, no
/// readable document, or the request fails.
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
        return [.. DefaultDescriptions
            .Select(description => new AssessmentChecklistItem
            {
                AssessmentId = assessmentId,
                Description = description,
                IsCompleted = false,
                SessionId = sessionId
            })];
    }
}
