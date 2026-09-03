namespace TimeManagement.Models;

/// <summary>
/// A single uploaded file associated with a specific assessment.
/// </summary>
public class Document
{
    public int Id { get; set; }

    public int AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    /// <summary>The filename as the user uploaded it, shown in the UI.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>The GUID-based filename the file is actually saved under on
    /// disk. Never derived from user input - see TextExtractionService for why.</summary>
    public string StoredFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Plain text pulled out of the file at upload time. Empty if extraction
    /// failed or isn't supported for the file type. This is used to drive the
    /// assessment classification and suggested due date flow.
    /// </summary>
    public string? ExtractedText { get; set; }
}
