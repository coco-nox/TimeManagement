using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TimeManagement.Services;

/// <summary>
/// Generates multiple-choice quiz questions from a course's uploaded document
/// text. Sent through <see cref="AiClient"/> as
/// <see cref="AiTask.QuizGeneration"/> - see the appsettings.json "AiRouting"
/// section for which provider handles it. Questions are generated on demand
/// and never stored - only a student's answers (QuizAttempt rows) are persisted.
/// </summary>
public sealed partial class QuizGenerationService(
    AiClient aiClient,
    ILogger<QuizGenerationService> logger)
{
    private readonly AiClient _aiClient = aiClient;
    private readonly ILogger<QuizGenerationService> _logger = logger;

    // Same budget reasoning as TutorChatService.BuildContext.
    private const int TotalContextCharBudget = 12_000;
    private const int PerDocumentCharBudget = 4_000;

    private const int QuestionsPerBatch = 5;
    private const int OptionsPerQuestion = 4;

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    /// <summary>
    /// Main entry point, called each time the Quiz tab needs a fresh batch of
    /// questions. Builds a prompt from the assessment's document text and
    /// asks the AI for structured JSON.
    /// </summary>
    public async Task<QuizGenerationResult> GenerateAsync(
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (!_aiClient.IsConfigured)
        {
            return new QuizGenerationResult(
                [],
                "The AI tutor isn't configured yet - ask an administrator to add a Gemini or Groq API key.");
        }

        var usableDocuments = documents.Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText)).ToList();
        if (usableDocuments.Count == 0)
        {
            return new QuizGenerationResult(
                [],
                "This assessment has no documents with readable text yet, so there's nothing to quiz you on.");
        }

        var context = BuildContext(usableDocuments);

        var request = new AiRequest(
            SystemInstruction:
                "You are a course tutor writing a multiple-choice quiz. Using ONLY the supplied " +
                "document excerpts, write exactly " + QuestionsPerBatch + " questions that test " +
                "understanding of the material - never invent facts not in the excerpts. Each " +
                "question needs exactly " + OptionsPerQuestion + " different answer options with exactly " +
                "one correct answer, plus a short topic label (2-4 words) categorising what the question " +
                "is about, so related questions share the same topic text. Return only valid JSON: an " +
                "object with one key \"questions\", an array of objects with keys \"question\" (string), " +
                "\"options\" (array of exactly " + OptionsPerQuestion + " strings), \"correctAnswer\" " +
                "(string, must exactly match one of the options), and \"topic\" (string).",
            Prompt: BuildPrompt(context),
            Temperature: 0.4,
            JsonOutput: true);

        // ParseResponse rejecting the reply (not valid JSON, or no valid
        // questions in it) makes AiClient retry on the other provider.
        var result = await _aiClient.AskAsync(AiTask.QuizGeneration, request, ParseResponse, cancellationToken);

        return result.Value is { } questions
            ? new QuizGenerationResult(questions, null)
            : new QuizGenerationResult([], AiClient.DescribeFailure(result, "AI tutor", "quiz", "quiz"));
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

    /// <summary>
    /// Validates the model's reply before any of it reaches the student. It
    /// must be JSON - either { "questions": [...] } (what the prompt asks for)
    /// or a bare array - and each question is only kept if it has question
    /// text, a topic, exactly <see cref="OptionsPerQuestion"/> distinct
    /// options, and a correctAnswer that matches one of them (normalised to
    /// that option's exact text, so the Quiz tab's answer check can't miss on
    /// spacing or case). Returns null when nothing valid is left, which
    /// AiClient treats as an unusable reply.
    /// </summary>
    private List<QuizQuestion>? ParseResponse(string aiResponse)
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
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var questionsEl))
            {
                root = questionsEl;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Quiz reply was JSON but had no questions array.");
                return null;
            }

            var questions = new List<QuizQuestion>();
            var rejected = 0;
            foreach (var element in root.EnumerateArray())
            {
                var question = TryReadQuestion(element);
                if (question == null)
                {
                    rejected++;
                    continue;
                }

                questions.Add(question);
            }

            if (rejected > 0)
            {
                _logger.LogWarning(
                    "Quiz reply had {Rejected} invalid question(s) that were dropped; {Kept} kept.",
                    rejected, questions.Count);
            }

            return questions.Count > 0 ? questions : null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Quiz reply was not valid JSON.");
            return null;
        }
    }

    private static QuizQuestion? TryReadQuestion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("question", out var questionEl) || questionEl.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("options", out var optionsEl) || optionsEl.ValueKind != JsonValueKind.Array ||
            !element.TryGetProperty("correctAnswer", out var correctEl) || correctEl.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("topic", out var topicEl) || topicEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var question = questionEl.GetString()?.Trim();
        var correctAnswer = correctEl.GetString()?.Trim();
        var topic = topicEl.GetString()?.Trim();

        if (string.IsNullOrEmpty(question) || string.IsNullOrEmpty(correctAnswer) || string.IsNullOrEmpty(topic))
        {
            return null;
        }

        var options = optionsEl.EnumerateArray()
            .Where(o => o.ValueKind == JsonValueKind.String)
            .Select(o => o.GetString()?.Trim())
            .Where(o => !string.IsNullOrEmpty(o))
            .Select(o => o!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (options.Count != OptionsPerQuestion)
        {
            return null;
        }

        var matchingOption = options.FirstOrDefault(o => string.Equals(o, correctAnswer, StringComparison.OrdinalIgnoreCase));
        if (matchingOption == null)
        {
            return null;
        }

        return new QuizQuestion(question, options, matchingOption, topic);
    }
}

/// <summary>One generated multiple-choice question. Never persisted - only
/// the student's answer (as a QuizAttempt) is.</summary>
public sealed record QuizQuestion(string Question, List<string> Options, string CorrectAnswer, string Topic);

public sealed record QuizGenerationResult(List<QuizQuestion> Questions, string? Error);
