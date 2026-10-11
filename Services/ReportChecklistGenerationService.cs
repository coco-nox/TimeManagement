using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// Designs a Report assessment's structure - the checklist the student
/// ticks off and the fuller outline (headings, sub-headings, word counts)
/// shown above it - in ONE AI call, so the two always agree. The AI works
/// like an experienced tutor would: it designs the report from its own
/// knowledge of how this kind of report is normally written for this
/// subject, then uses the assessment's uploaded brief/rubric as the
/// reference that every graded requirement must be covered by some section.
/// It is not limited to what the brief spells out - sections the brief
/// doesn't mention but a strong report would include are added and marked
/// as Suggested rather than Brief. With no readable document it still
/// designs a report from the course and assessment title alone. Reuses the
/// same AI provider config as the other AI features (same appsettings.json
/// "Gemini" section, same HTTP endpoint) rather than a separate integration.
///
/// Always returns a usable checklist, never an error the caller has to
/// handle: with no AI configured or a request that fails outright,
/// <see cref="BuildReportPlanAsync"/> falls back to
/// <see cref="AssessmentChecklistItem.DefaultDescriptions"/> (with no
/// outline) instead of leaving the assessment with nothing. That matters
/// because this runs as a side effect of uploading a document or archiving a
/// checklist, not as its own user-facing action with a retry button - it
/// should never be the reason one of those actions fails.
/// </summary>
public sealed partial class ReportChecklistGenerationService(
    HttpClient httpClient,
    IOptions<DocumentCategorizationOptions> options,
    ILogger<ReportChecklistGenerationService> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<ReportChecklistGenerationService> _logger = logger;
    private readonly DocumentCategorizationOptions _options = options.Value;

    // Same budget reasoning as TutorChatService.BuildContext.
    private const int TotalContextCharBudget = 12_000;
    private const int PerDocumentCharBudget = 4_000;

    private const int MaxSections = 10;
    private const int MaxListEntries = 6;
    private const int MaxDescriptionLength = 300;
    private const int MaxGuidanceLength = 500;
    private const int MaxShortTextLength = 150;

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    /// <summary>
    /// Main entry point, called wherever a Report assessment needs a fresh
    /// checklist session (first upload, first view of an empty checklist, or
    /// archiving into a new session). Returns checklist items sharing one new
    /// SessionId plus the matching outline (null on the default fallback),
    /// ready to add to the database.
    /// </summary>
    public async Task<ReportPlan> BuildReportPlanAsync(
        int assessmentId,
        string courseTitle,
        string assessmentTitle,
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid();
        var design = await GenerateDesignAsync(courseTitle, assessmentTitle, documents, cancellationToken);

        if (design == null || design.Sections.Count == 0)
        {
            return new ReportPlan(
                [.. AssessmentChecklistItem.DefaultDescriptions.Select(description => new AssessmentChecklistItem
                {
                    AssessmentId = assessmentId,
                    Description = description,
                    IsCompleted = false,
                    SessionId = sessionId
                })],
                null);
        }

        var items = design.Sections.Select(section => new AssessmentChecklistItem
        {
            AssessmentId = assessmentId,
            Description = BuildDescription(section),
            Guidance = section.Guidance,
            Source = section.Source,
            IsCompleted = false,
            SessionId = sessionId
        }).ToList();

        var outline = new ReportOutline
        {
            AssessmentId = assessmentId,
            SessionId = sessionId,
            ReportType = design.ReportType,
            SuggestedTotalWords = design.SuggestedTotalWords,
            CreatedUtc = DateTime.UtcNow
        };
        outline.SetSections(design.Sections);

        return new ReportPlan(items, outline);
    }

    // "Requirements analysis" + "15%" -> "Requirements analysis (15%)",
    // unless the AI already put the weight in the title itself.
    private static string BuildDescription(ReportOutlineSection section)
    {
        var description = section.Title;
        if (!string.IsNullOrWhiteSpace(section.Weight) && !description.Contains(section.Weight, StringComparison.OrdinalIgnoreCase))
        {
            description = $"{description} ({section.Weight})";
        }

        return Truncate(description, MaxDescriptionLength);
    }

    /// <summary>Returns null on anything short of a clean, parsed answer -
    /// BuildReportPlanAsync treats that as "fall back to defaults" rather
    /// than needing a separate success/failure signal.</summary>
    private async Task<ReportDesign?> GenerateDesignAsync(
        string courseTitle,
        string assessmentTitle,
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return null;
        }

        // No readable document is no longer a reason to skip the AI: it can
        // still design a sensible report from the course and assessment
        // title, it just has nothing to cross-check against.
        var usableDocuments = documents.Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText)).ToList();
        var context = usableDocuments.Count > 0 ? BuildContext(usableDocuments) : string.Empty;
        var prompt = BuildPrompt(courseTitle, assessmentTitle, context);

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = "You are an experienced university tutor helping a student plan a Report-category " +
                               "assessment. Design the structure of the report they should write, in two steps. " +
                               "STEP 1 - use your own expertise: work out what kind of report this is for this " +
                               "subject (e.g. technical project report, lab report, business case, literature " +
                               "review, reflective report) and design the structure a strong, high-scoring report " +
                               "of that kind normally has - sections in the order they'd appear, sensible " +
                               "sub-headings, and what each part should contain. You are not limited to what the " +
                               "brief spells out. Briefs usually leave the standard parts of a report implicit - " +
                               "things like an executive summary or abstract, a conclusion, references, or an " +
                               "appendix (e.g. code listings, raw data) - so explicitly consider those for this " +
                               "report type and add the ones that would genuinely strengthen it (typically one to " +
                               "three), unless the brief says to follow a fixed template or forbids extra sections. " +
                               "STEP 2 - use the assignment brief/rubric excerpts " +
                               "(when supplied) as the reference: check every graded component, required section, " +
                               "deliverable, word limit and mark weighting in the brief, and make sure each one is " +
                               "covered by a section - adjust, rename or add sections until nothing the brief " +
                               "requires is missing, and use the brief's own section names where it gives them. " +
                               "Mark a section \"brief\" if the brief requires it, or \"suggested\" if you added it " +
                               "from your own expertise. Never claim the brief says something it doesn't: " +
                               "\"briefRequirements\" may only list requirements actually found in the excerpts " +
                               "(short paraphrases), and must be empty for a suggested section or when no excerpts " +
                               "were supplied. If the brief gives a word limit, use it for suggestedTotalWords and " +
                               "split it across sections; otherwise estimate a typical length for this kind of " +
                               "report. Produce between 4 and " + MaxSections + " top-level sections, each with up " +
                               "to " + MaxListEntries + " sub-headings. \"guidance\" is one or two plain sentences " +
                               "(under 50 words) on what to actually write in that section, specific to this " +
                               "assessment rather than generic advice. Return only valid JSON, an object with keys: " +
                               "\"reportType\" (string), \"suggestedTotalWords\" (integer or null), and " +
                               "\"sections\" (array of objects with keys \"title\" (string, short), \"weight\" " +
                               "(string like \"20%\" or \"15 marks\" when the brief gives one, else null), " +
                               "\"wordCount\" (integer or null), \"source\" (\"brief\" or \"suggested\"), " +
                               "\"guidance\" (string), \"subsections\" (array of strings), and " +
                               "\"briefRequirements\" (array of strings))."
                    }
                }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { temperature = 0.3, responseMimeType = "application/json" }
        };

        _httpClient.DefaultRequestHeaders.Remove("x-goog-api-key");
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _options.ApiKey);

        var requestUri = $"{_options.Endpoint}/{_options.Model}:generateContent";

        try
        {
            var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

            // Gemini answers 503 ("overloaded") often enough to matter, and
            // there's no retry button here - one short retry before giving
            // up and falling back to the default checklist.
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);
            }

            using var _ = response;
            if (!response.IsSuccessStatusCode)
            {
                var failureBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Report plan generation request to Gemini failed with HTTP {StatusCode}. Response body: {Body}",
                    (int)response.StatusCode, failureBody);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = JsonDocument.Parse(responseJson);
            var root = json.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
            {
                var parts = candidates[0].GetProperty("content").GetProperty("parts");
                var assistantText = parts.GetArrayLength() > 0 ? parts[0].GetProperty("text").GetString() : null;

                if (!string.IsNullOrWhiteSpace(assistantText))
                {
                    return ParseResponse(assistantText);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            // Swallowed deliberately - see the class summary: this always
            // degrades to the default checklist rather than failing the
            // upload/archive action it's running inside of.
            _logger.LogWarning(ex, "Report plan generation failed; falling back to the default checklist.");
        }

        return null;
    }

    // Same excerpt-concatenation approach as TutorChatService.BuildContext.
    private static string BuildContext(IReadOnlyList<TutorSourceDocument> documents)
    {
        var builder = new StringBuilder();
        var remainingBudget = TotalContextCharBudget;

        foreach (var document in documents)
        {
            if (remainingBudget <= 0)
            {
                break;
            }

            var text = document.ExtractedText!.Trim();
            var perDocumentLimit = Math.Min(PerDocumentCharBudget, remainingBudget);
            if (text.Length > perDocumentLimit)
            {
                text = text[..perDocumentLimit];
            }

            builder.Append("=== Source: ").Append(document.SourceName).Append(" ===\n")
                   .Append(text).Append("\n\n");

            remainingBudget -= text.Length;
        }

        return builder.ToString();
    }

    private static string BuildPrompt(string courseTitle, string assessmentTitle, string context)
    {
        if (string.IsNullOrEmpty(context))
        {
            return $"Course: {courseTitle}\nAssessment: {assessmentTitle}\n\n" +
                   "No assignment brief with readable text has been uploaded - design the report from your own " +
                   "expertise for this course and assessment, mark every section \"suggested\", and leave every " +
                   "briefRequirements list empty.";
        }

        return $"Course: {courseTitle}\nAssessment: {assessmentTitle}\n\nAssignment brief / document excerpts:\n\n{context}";
    }

    private static ReportDesign? ParseResponse(string aiResponse)
    {
        var text = aiResponse.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = JsonFenceRemover().Replace(text, string.Empty);
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sections", out var sectionsEl) ||
                sectionsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sections = new List<ReportOutlineSection>();
            foreach (var element in sectionsEl.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = ReadString(element, "title", MaxShortTextLength);
                if (title == null)
                {
                    continue;
                }

                var source = string.Equals(ReadString(element, "source", 20), "brief", StringComparison.OrdinalIgnoreCase)
                    ? ReportSectionSource.Brief
                    : ReportSectionSource.Suggested;

                sections.Add(new ReportOutlineSection(
                    title,
                    ReadString(element, "weight", 30),
                    ReadPositiveInt(element, "wordCount"),
                    source,
                    ReadString(element, "guidance", MaxGuidanceLength),
                    ReadStringList(element, "subsections", MaxShortTextLength),
                    // A suggested section can't cite the brief, whatever the AI sent.
                    source == ReportSectionSource.Brief ? ReadStringList(element, "briefRequirements", MaxDescriptionLength) : []));

                if (sections.Count >= MaxSections)
                {
                    break;
                }
            }

            return new ReportDesign(
                ReadString(root, "reportType", 200),
                ReadPositiveInt(root, "suggestedTotalWords"),
                sections);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : Truncate(text, maxLength);
    }

    // Tolerates "1500" as well as 1500 - the AI isn't always strict about
    // number vs string.
    private static int? ReadPositiveInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        int number;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number) && number > 0)
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) && number > 0)
        {
            return number;
        }

        return null;
    }

    private static List<string> ReadStringList(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => Truncate(s!, maxLength))
            .Take(MaxListEntries)];
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..maxLength] : text;

    private sealed record ReportDesign(string? ReportType, int? SuggestedTotalWords, List<ReportOutlineSection> Sections);
}

/// <summary>A new checklist session for a Report assessment plus its
/// matching outline. Outline is null when the default checklist was used
/// (no AI configured, or the request failed).</summary>
public sealed record ReportPlan(List<AssessmentChecklistItem> ChecklistItems, ReportOutline? Outline);
