using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TimeManagement.Services;

/// <summary>
/// Generates multiple-choice quiz questions from a course's uploaded document
/// text. Reuses the same AI provider config as
/// <see cref="DocumentCategorizationService"/> and <see cref="TutorChatService"/>
/// (same appsettings.json "Gemini" section, same HTTP endpoint) rather than a
/// third AI integration. Questions are generated on demand and never stored -
/// only a student's answers (QuizAttempt rows) are persisted.
/// </summary>
public sealed partial class QuizGenerationService(HttpClient httpClient, IOptions<DocumentCategorizationOptions> options)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly DocumentCategorizationOptions _options = options.Value;

    // Same budget reasoning as TutorChatService.BuildContext.
    private const int TotalContextCharBudget = 12_000;
    private const int PerDocumentCharBudget = 4_000;

    private const int QuestionsPerBatch = 5;

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    /// <summary>
    /// Main entry point, called each time the Quiz tab needs a fresh batch of
    /// questions. Builds a prompt from the assessment's document text and
    /// asks Gemini for structured JSON.
    /// </summary>
    public async Task<QuizGenerationResult> GenerateAsync(
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return new QuizGenerationResult(
                [],
                "The AI tutor isn't configured yet - ask an administrator to set the DocumentCategorization API key in appsettings.json.");
        }

        var usableDocuments = documents.Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText)).ToList();
        if (usableDocuments.Count == 0)
        {
            return new QuizGenerationResult(
                [],
                "This assessment has no documents with readable text yet, so there's nothing to quiz you on.");
        }

        var context = BuildContext(usableDocuments);
        var prompt = BuildPrompt(context);

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = $"You are a course tutor writing a multiple-choice quiz. Using ONLY the supplied " +
                               "document excerpts, write exactly " + QuestionsPerBatch + " questions that test " +
                               "understanding of the material - never invent facts not in the excerpts. Each " +
                               "question needs exactly 4 answer options with exactly one correct answer, plus a " +
                               "short topic label (2-4 words) categorising what the question is about, so related " +
                               "questions share the same topic text. Return only valid JSON: an array of objects " +
                               "with keys \"question\" (string), \"options\" (array of exactly 4 strings), " +
                               "\"correctAnswer\" (string, must exactly match one of the options), and \"topic\" (string)."
                    }
                }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { temperature = 0.4 }
        };

        _httpClient.DefaultRequestHeaders.Remove("x-goog-api-key");
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _options.ApiKey);

        var requestUri = $"{_options.Endpoint}/{_options.Model}:generateContent";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Same daily-vs-transient distinction as TutorChatService.AskAsync.
                if (errorBody.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                {
                    return new QuizGenerationResult(
                        [],
                        "The AI tutor has reached its daily question limit. It resets at midnight " +
                        "Pacific Time - please try again after that.");
                }

                return new QuizGenerationResult([], "The AI tutor is being rate-limited right now. Please wait a moment and try again.");
            }

            return new QuizGenerationResult([], $"The quiz request failed (HTTP {(int)response.StatusCode}). Please try again.");
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
                var questions = ParseResponse(assistantText);
                if (questions is { Count: > 0 })
                {
                    return new QuizGenerationResult(questions, null);
                }
            }
        }

        return new QuizGenerationResult([], "The AI tutor didn't return a usable quiz. Please try again.");
    }

    // Same excerpt-concatenation approach as TutorChatService.BuildContext,
    // minus source citation (quiz questions don't need to name a source file).
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

    private static string BuildPrompt(string context)
    {
        return "Course document excerpts:\n\n" + context;
    }

    // Parses the model's reply as a JSON array of question objects. Returns
    // null (not a thrown exception) for anything that doesn't fit, so
    // GenerateAsync can fall back to its "didn't return a usable quiz" message.
    private static List<QuizQuestion>? ParseResponse(string aiResponse)
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
                return null;
            }

            var questions = new List<QuizQuestion>();
            foreach (var element in root.EnumerateArray())
            {
                if (!element.TryGetProperty("question", out var questionEl) ||
                    !element.TryGetProperty("options", out var optionsEl) ||
                    !element.TryGetProperty("correctAnswer", out var correctEl) ||
                    !element.TryGetProperty("topic", out var topicEl))
                {
                    continue;
                }

                var question = questionEl.GetString();
                var correctAnswer = correctEl.GetString();
                var topic = topicEl.GetString();

                if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(correctAnswer) ||
                    string.IsNullOrWhiteSpace(topic) || optionsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var options = optionsEl.EnumerateArray()
                    .Select(o => o.GetString())
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Select(o => o!.Trim())
                    .ToList();

                if (options.Count < 2)
                {
                    continue;
                }

                questions.Add(new QuizQuestion(question.Trim(), options, correctAnswer.Trim(), topic.Trim()));
            }

            return questions.Count > 0 ? questions : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One generated multiple-choice question. Never persisted - only
/// the student's answer (as a QuizAttempt) is.</summary>
public sealed record QuizQuestion(string Question, List<string> Options, string CorrectAnswer, string Topic);

public sealed record QuizGenerationResult(List<QuizQuestion> Questions, string? Error);
