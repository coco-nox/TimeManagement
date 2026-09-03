namespace TimeManagement.Models;

/// <summary>
/// A course a user is studying (e.g. "ITEC631"). Each course owns one or
/// more assessments, and each assessment owns the documents uploaded for it.
/// </summary>
public class Course
{
    public int Id { get; set; }

    /// <summary>The owning user. Every query must filter on this so a user
    /// can never see another user's courses.</summary>
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<Assessment> Assessments { get; set; } = new();
}
