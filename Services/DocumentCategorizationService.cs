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

    public async Task<AssessmentCategorizationResult> CategorizeAsync(string extractedText, CancellationToken cancellationToken = default)
    {
        var normalizedText = string.IsNullOrWhiteSpace(extractedText) ? string.Empty : extractedText.Trim();
        var keywordCategory = InferCategoryFromKeywords(normalizedText);

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

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return new AssessmentCategorizationResult(
                Category: keywordCategory ?? AssessmentCategory.Coursework,
                DueDate: null,
                WasDetected: keywordCategory != null,
                Reason: keywordCategory != null
                    ? "AI key missing; used keyword fallback classification."
                    : "No AI key configured or no text available for analysis.");
        }

        var prompt = BuildPrompt(normalizedText);
        var payload = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = "Return only valid JSON with keys category and dueDate. category must be one of Coursework, Quiz, Report, Test. dueDate must be null if no due date is mentioned, otherwise ISO date string YYYY-MM-DD." },
                new { role = "user", content = prompt }
            },
            temperature = 0.1
        };

        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");

        using var response = await _httpClient.PostAsJsonAsync(_options.Endpoint, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new AssessmentCategorizationResult(
                Category: keywordCategory ?? AssessmentCategory.Coursework,
                DueDate: null,
                WasDetected: keywordCategory != null,
                Reason: keywordCategory != null
                    ? "AI categorization failed; used keyword fallback classification."
                    : $"AI categorization failed with HTTP {(int)response.StatusCode}.");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonDocument.Parse(responseJson);
        var content = json.RootElement;

        if (content.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            var assistantText = message.GetProperty("content").GetString();

            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                var parsed = ParseResponse(assistantText);
                if (parsed != null)
                {
                    return parsed;
                }
            }
        }

        return new AssessmentCategorizationResult(
            Category: keywordCategory ?? AssessmentCategory.Coursework,
            DueDate: null,
            WasDetected: keywordCategory != null,
            Reason: keywordCategory != null
                ? "The AI response did not contain a usable classification, so keyword matching was used instead."
                : "The AI response did not contain a usable classification.");
    }

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

    private static string BuildPrompt(string extractedText)
    {
        var sample = extractedText.Length > 5000 ? extractedText[..5000] : extractedText;
        return "Review the following student document text and decide which assessment category it best matches. " +
               "Return only JSON: { \"category\": \"Coursework\" | \"Quiz\" | \"Report\" | \"Test\", \"dueDate\": \"YYYY-MM-DD\" | null }. " +
               "If no due date is mentioned, use null. " +
               "Use the text to infer the category, not the filename.\n\nText:\n" + sample;
    }

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

    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";

    public string Model { get; set; } = "gpt-4o-mini";
}

public sealed record AssessmentCategorizationResult(
    AssessmentCategory Category,
    DateTime? DueDate,
    bool WasDetected,
    string Reason);
