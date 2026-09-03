namespace TimeManagement.Models;

public enum AssessmentCategory
{
    Coursework,
    Quiz,
    Report,
    Test
}

/// <summary>
/// A named assessment within a course, such as an assignment, quiz, report,
/// or exam. Documents are uploaded against an assessment instead of directly
/// against the course.
/// </summary>
public class Assessment
{
    public int Id { get; set; }

    public int CourseId { get; set; }

    public Course? Course { get; set; }

    public string Title { get; set; } = string.Empty;

    public AssessmentCategory Category { get; set; } = AssessmentCategory.Coursework;

    public DateTime? DueDate { get; set; }

    public bool DueDateConfirmed { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<Document> Documents { get; set; } = new();
}
