using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// Builds a Report assessment's completion checklist from the actual
/// rubric or requirements in its own uploaded document(s), read alongside
/// which course the assessment belongs to - instead of the fixed, generic
/// section list every Report used to get regardless of what the assignment
/// brief actually asked for. Reuses the same AI provider config as the
/// other AI features (same appsettings.json "Gemini" section, same HTTP
/// endpoint) rather than a separate integration.
///
/// Always returns a usable checklist, never an error the caller has to
/// handle: with no AI configured, no readable documents, or a request that
/// fails outright, <see cref="BuildChecklistAsync"/> falls back to
/// <see cref="AssessmentChecklistItem.DefaultDescriptions"/> instead of
/// leaving the assessment with nothing. That matters because this runs as
/// a side effect of uploading a document or archiving a checklist, not as
/// its own user-facing action with a retry button - it should never be the
/// reason one of those actions fails.
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

    private const int MaxItems = 8;
    private const int MaxItemLength = 300;

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    /// <summary>
    /// Main entry point, called wherever a Report assessment needs a fresh
    /// checklist session (first upload, recategorised-to-Report fallback,
    /// or archiving into a new session). Returns items with one shared new
    /// SessionId, ready to add to the database.
    /// </summary>
    public async Task<List<AssessmentChecklistItem>> BuildChecklistAsync(
        int assessmentId,
        string courseTitle,
        string assessmentTitle,
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var sections = await GenerateSectionsAsync(courseTitle, assessmentTitle, documents, cancellationToken);
        IReadOnlyList<string> descriptions = sections.Count > 0 ? sections : AssessmentChecklistItem.DefaultDescriptions;

        var sessionId = Guid.NewGuid();
        return [.. descriptions.Select(description => new AssessmentChecklistItem
        {
            AssessmentId = assessmentId,
            Description = description,
            IsCompleted = false,
            SessionId = sessionId
        })];
    }

    /// <summary>Returns an empty list on anything short of a clean, parsed
    /// answer - BuildChecklistAsync treats that as "fall back to defaults"
    /// rather than needing a separate success/failure signal.</summary>
    private async Task<List<string>> GenerateSectionsAsync(
        string courseTitle,
        string assessmentTitle,
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return [];
        }

        var usableDocuments = documents.Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText)).ToList();
        if (usableDocuments.Count == 0)
        {
            return [];
        }

        var context = BuildContext(usableDocuments);
        var prompt = BuildPrompt(courseTitle, assessmentTitle, context);

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = "You are helping a student track their progress on a Report-category assessment. " +
                               "Read the supplied assignment brief/rubric excerpts for their course and produce a " +
                               "checklist of the concrete sections or deliverables they actually need to complete, " +
                               "in the order they'd naturally work through them. Each item must be one short line " +
                               "(under 20 words) that names the section AND the specific requirement or grading " +
                               "criterion the brief gives for it - never a bare generic label like \"Introduction\" " +
                               "on its own when the brief says more than that. If the brief states a grading weight " +
                               "or mark allocation for a section, include it in parentheses, e.g. \"Testing: include " +
                               "unit tests and a documented test plan (20%)\". Produce between 4 and 8 items that " +
                               "together cover the whole assessment - don't pad with items the brief doesn't call " +
                               "for, and don't leave out a graded component the brief clearly requires. If the " +
                               "excerpts don't actually contain a rubric or clear structure, fall back to sensible " +
                               "standard report sections for the subject instead of inventing specifics that aren't " +
                               "there. Return only valid JSON: an array of strings, one per checklist item, nothing else."
                    }
                }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { temperature = 0.2 }
        };

        _httpClient.DefaultRequestHeaders.Remove("x-goog-api-key");
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _options.ApiKey);

        var requestUri = $"{_options.Endpoint}/{_options.Model}:generateContent";

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failureBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Report checklist generation request to Gemini failed with HTTP {StatusCode}. Response body: {Body}",
                    (int)response.StatusCode, failureBody);
                return [];
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
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Swallowed deliberately - see the class summary: this always
            // degrades to the default checklist rather than failing the
            // upload/archive action it's running inside of.
            _logger.LogWarning(ex, "Report checklist generation failed; falling back to the default checklist.");
        }

        return [];
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
        return $"Course: {courseTitle}\nAssessment: {assessmentTitle}\n\nAssignment brief / document excerpts:\n\n{context}";
    }

    private static List<string> ParseResponse(string aiResponse)
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
            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var items = new List<string>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                value = value.Trim();
                if (value.Length > MaxItemLength)
                {
                    value = value[..MaxItemLength];
                }

                items.Add(value);
                if (items.Count >= MaxItems)
                {
                    break;
                }
            }

            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
