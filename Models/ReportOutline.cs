using System.Text.Json;

namespace TimeManagement.Models;

/// <summary>
/// The full AI-designed structure for a Report assessment - headings,
/// sub-headings, suggested word counts, and which parts of the brief each
/// section covers. Generated in the same AI call as the checklist (see
/// Services/ReportChecklistGenerationService.cs), so the two always agree:
/// one outline per checklist session, linked by SessionId, and a fresh one
/// is built whenever the checklist is archived and re-seeded.
/// </summary>
public class ReportOutline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public int Id { get; set; }

    public int AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    /// <summary>Matches the AssessmentChecklistItem.SessionId this outline
    /// was generated alongside.</summary>
    public Guid SessionId { get; set; }

    /// <summary>What kind of report the AI judged this to be, e.g.
    /// "Technical project report".</summary>
    public string? ReportType { get; set; }

    /// <summary>The brief's word limit when it gives one, otherwise the AI's
    /// estimate for this kind of report.</summary>
    public int? SuggestedTotalWords { get; set; }

    /// <summary>The sections, stored as JSON - they're only ever read and
    /// shown as a whole, never queried individually, so a child table would
    /// add joins without buying anything.</summary>
    public string SectionsJson { get; set; } = "[]";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<ReportOutlineSection> GetSections()
    {
        try
        {
            return JsonSerializer.Deserialize<List<ReportOutlineSection>>(SectionsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void SetSections(List<ReportOutlineSection> sections)
    {
        SectionsJson = JsonSerializer.Serialize(sections);
    }
}

/// <summary>One top-level heading of a report outline.</summary>
public sealed record ReportOutlineSection(
    string Title,
    string? Weight,
    int? WordCount,
    ReportSectionSource Source,
    string? Guidance,
    List<string> Subsections,
    List<string> BriefRequirements);
