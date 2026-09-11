using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// Best-effort AI categorisation for uploaded documents. It does not replace
/// a user review step: the AI suggests a category and due date, then the app
/// stores that suggestion on the created assessment and leaves the due date
/// unconfirmed unless the text explicitly contains one and it was accepted.
/// </summary>
public sealed partial class DocumentCategorizationService(HttpClient httpClient, IOptions<DocumentCategorizationOptions> options)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly DocumentCategorizationOptions _options = options.Value;

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    // Looks for a due-date-style label ("Due date", "Deadline", "Submission
    // date", "Hand-in date", ...) immediately followed by a date in one of a
    // handful of common written formats. This runs independently of the AI
    // call so a due date can still be found when there's no API key
    // configured, the AI request fails, or the AI just doesn't notice it -
    // see CategorizeAsync for where each of those falls back to it.
    [GeneratedRegex(
        "(?:due\\s*date|due|deadline|submission\\s*date|hand[\\s-]?in\\s*date)\\s*(?:is|:|-)?\\s*" +
        "(?<date>\\d{1,2}(?:st|nd|rd|th)?\\s+[A-Za-z]+\\s+\\d{4}" +
        "|[A-Za-z]+\\s+\\d{1,2}(?:st|nd|rd|th)?,?\\s+\\d{4}" +
        "|\\d{4}-\\d{1,2}-\\d{1,2}" +
        "|\\d{1,2}[\\/.-]\\d{1,2}[\\/.-]\\d{2,4})",
        RegexOptions.IgnoreCase)]
    private static partial Regex DueDateLabelPattern();

    [GeneratedRegex("(?<=\\d)(st|nd|rd|th)", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSuffixRemover();

    /// <summary>
    /// Main entry point, called once per uploaded document. Tries the AI
    /// first and falls back to simple keyword matching whenever the AI
    /// can't be used (no key, no text) or fails, so an upload never ends up
    /// with no category suggestion at all.
    /// </summary>
    public async Task<AssessmentCategorizationResult> CategorizeAsync(string extractedText, CancellationToken cancellationToken = default)
    {
        var normalizedText = string.IsNullOrWhiteSpace(extractedText) ? string.Empty : extractedText.Trim();
        var keywordCategory = InferCategoryFromKeywords(normalizedText);
        var regexDueDate = TryExtractDueDateFromText(normalizedText);

        // No text to work with (extraction failed or produced nothing) -
        // keyword matching has nothing to search either, so skip straight
        // to a default rather than calling the AI with an empty prompt.
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return new AssessmentCategorizationResult(
                Category: keywordCategory ?? AssessmentCategory.Coursework,
                DueDate: null,
                WasDetected: keywordCategory != null,
                Reason: keywordCategory != null
                    ? "No text was available, so the category was inferred from the file name or empty content fallback."
                    : "No AI key configured or no text available for analysis.");
        }

        // No API key configured - same fallback, but text was available so
        // the keyword pass (and the due-date regex below) had something
        // real to work with.
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return new AssessmentCategorizationResult(
                Category: keywordCategory ?? AssessmentCategory.Coursework,
                DueDate: regexDueDate,
                WasDetected: keywordCategory != null || regexDueDate != null,
                Reason: keywordCategory != null
                    ? "AI key missing; used keyword fallback classification."
                    : "No AI key configured or no text available for analysis.");
        }

        // Build and send the Gemini request: a short system instruction plus
        // the document text, asking for category + due date as JSON.
        var prompt = BuildPrompt(normalizedText);
        var payload = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new { text = "Return only valid JSON with keys category and dueDate. category must be one of Coursework, Quiz, Report, Test. dueDate must be null if no due date is mentioned, otherwise ISO date string YYYY-MM-DD." }
                }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { temperature = 0.1 }
        };

        _httpClient.DefaultRequestHeaders.Remove("x-goog-api-key");
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _options.ApiKey);

        var requestUri = $"{_options.Endpoint}/{_options.Model}:generateContent";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new AssessmentCategorizationResult(
                Category: keywordCategory ?? AssessmentCategory.Coursework,
                DueDate: regexDueDate,
                WasDetected: keywordCategory != null || regexDueDate != null,
                Reason: keywordCategory != null
                    ? "AI categorization failed; used keyword fallback classification."
                    : $"AI categorization failed with HTTP {(int)response.StatusCode}.");
        }

        // Pull the model's reply text out of Gemini's response shape
        // (candidates[0].content.parts[0].text) and try to parse it as the
        // { category, dueDate } JSON we asked for.
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonDocument.Parse(responseJson);
        var content = json.RootElement;

        if (content.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            var assistantText = parts.GetArrayLength() > 0 ? parts[0].GetProperty("text").GetString() : null;

            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                var parsed = ParseResponse(assistantText);
                if (parsed != null)
                {
                    // The AI missed a due date the regex pass found in the
                    // text (a common gap - see BuildPrompt's 5,000-character
                    // cap, or the AI simply not recognising the wording used).
                    // Trust the regex hit rather than leaving it blank.
                    if (parsed.DueDate == null && regexDueDate != null)
                    {
                        return parsed with
                        {
                            DueDate = regexDueDate,
                            WasDetected = true,
                            Reason = parsed.Reason + " A due date was found by pattern matching instead."
                        };
                    }

                    return parsed;
                }
            }
        }

        return new AssessmentCategorizationResult(
            Category: keywordCategory ?? AssessmentCategory.Coursework,
            DueDate: regexDueDate,
            WasDetected: keywordCategory != null || regexDueDate != null,
            Reason: keywordCategory != null
                ? "The AI response did not contain a usable classification, so keyword matching was used instead."
                : "The AI response did not contain a usable classification.");
    }

    // Deterministic due-date scanner: finds the first "Due date: <date>"
    // style label in the text and parses whatever date follows it. Runs
    // regardless of whether the AI is used, so a due date the AI doesn't
    // catch (or can't be asked about, with no API key configured) still has
    // a chance of being found - see CategorizeAsync for how the two are combined.
    private static DateTime? TryExtractDueDateFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DueDateLabelPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var dateText = OrdinalSuffixRemover().Replace(match.Groups["date"].Value, string.Empty).Trim();

        if (DateTime.TryParseExact(dateText, "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDate))
        {
            return isoDate.Date;
        }

        if (DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDate))
        {
            return parsedDate.Date;
        }

        return null;
    }

    // Fallback classifier for when the AI can't be used: looks for a handful
    // of category-specific words/phrases in the document text. Checked in a
    // fixed order (Quiz, Test, Report, Coursework) so a document mentioning
    // more than one just takes the first match.
    private static AssessmentCategory? InferCategoryFromKeywords(string extractedText)
    {
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            return null;
        }

        var normalized = extractedText.ToLowerInvariant();

        if (ContainsAny(normalized, "quiz", "multiple choice", "mcq", "multiple-choice", "short answer quiz"))
        {
            return AssessmentCategory.Quiz;
        }

        if (ContainsAny(normalized, "test", "exam", "midterm", "final exam", "final assessment"))
        {
            return AssessmentCategory.Test;
        }

        if (ContainsAny(normalized, "report", "lab report", "research report", "case report", "study report"))
        {
            return AssessmentCategory.Report;
        }

        if (ContainsAny(normalized, "coursework", "assignment", "essay", "homework", "portfolio", "project task"))
        {
            return AssessmentCategory.Coursework;
        }

        return null;
    }

    // True if any of the given tokens appears anywhere in value.
    private static bool ContainsAny(string value, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // The user-turn text sent to Gemini: instructions plus up to the first
    // 5,000 characters of the document (long enough for context, short
    // enough to keep the request cheap and fast).
    private static string BuildPrompt(string extractedText)
    {
        var sample = extractedText.Length > 5000 ? extractedText[..5000] : extractedText;
        return "Review the following student document text and decide which assessment category it best matches. " +
               "Return only JSON: { \"category\": \"Coursework\" | \"Quiz\" | \"Report\" | \"Test\", \"dueDate\": \"YYYY-MM-DD\" | null }. " +
               "If no due date is mentioned, use null. " +
               "Use the text to infer the category, not the filename.\n\nText:\n" + sample;
    }

    // Parses the model's reply as { category, dueDate } JSON. Returns null
    // (not a thrown exception) for anything that doesn't fit the expected
    // shape, so the caller can fall back to the keyword result instead.
    private static AssessmentCategorizationResult? ParseResponse(string aiResponse)
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

            if (!root.TryGetProperty("category", out var categoryElement) || !root.TryGetProperty("dueDate", out var dueDateElement))
            {
                return null;
            }

            var categoryText = categoryElement.GetString();
            var dueDateText = dueDateElement.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(dueDateElement.GetString())
                ? null
                : dueDateElement.GetString();

            if (!Enum.TryParse<AssessmentCategory>(categoryText, ignoreCase: true, out var category))
            {
                return null;
            }

            DateTime? dueDate = null;
            if (!string.IsNullOrWhiteSpace(dueDateText))
            {
                if (DateTime.TryParseExact(dueDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    dueDate = parsedDate;
                }
                else if (DateTime.TryParse(dueDateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedFallback))
                {
                    dueDate = parsedFallback.Date;
                }
            }

            return new AssessmentCategorizationResult(category, dueDate, dueDate != null, "AI categorization complete.");
        }
        catch
        {
            return null;
        }
    }
}

public sealed class DocumentCategorizationOptions
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL for the Gemini generateContent API, without the trailing
    /// "/{model}:generateContent" - each request appends that using <see cref="Model"/>.</summary>
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";

    public string Model { get; set; } = "gemini-3.6-flash";
}

public sealed record AssessmentCategorizationResult(
    AssessmentCategory Category,
    DateTime? DueDate,
    bool WasDetected,
    string Reason);
