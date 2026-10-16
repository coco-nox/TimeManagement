namespace TimeManagement.Models;

/// <summary>
/// A paper the student writes in the Tutor's Write tab (a report draft, an
/// essay, notes), stored with its course and optionally the assessment it's
/// for. ContentHtml is the editor's HTML, always sanitised on save (see
/// <see cref="Services.DocumentHtml"/>) - it's rendered back into the page,
/// so nothing a browser would run may survive in it.
/// </summary>
public class StudyDocument
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public int CourseId { get; set; }

    public Course? Course { get; set; }

    public int? AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ContentHtml { get; set; } = string.Empty;

    public int WordCount { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
