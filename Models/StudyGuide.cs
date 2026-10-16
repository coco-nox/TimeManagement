namespace TimeManagement.Models;

/// <summary>
/// The AI-written study guide for one Test or Quiz assessment (Tutor ›
/// Study guide). One per assessment - Regenerate replaces it. ContentJson
/// is the validated AI reply (see <see cref="Services.StudyGuideContent"/>),
/// rendered to HTML on demand for the page, print/PDF and Word.
/// </summary>
public class StudyGuide
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public int AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    public string ContentJson { get; set; } = string.Empty;

    /// <summary>The documents it was written from, for "built from: …".</summary>
    public string SourceNames { get; set; } = string.Empty;

    public DateTime GeneratedUtc { get; set; }
}
