using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TimeManagement.Services;

/// <summary>
/// Answers a student's question about a course like an actual tutor would:
/// grounded in that course's uploaded Coursework-category documents (see
/// Pages/Tutor/Index.cshtml.cs OnPostAskAsync, which builds the document
/// list this service receives - Report/Quiz/Test attachments are left out
/// so an answer can't be built on material the class hasn't reached yet)
/// when they're relevant, but not limited to quoting them. The model is
/// free to use its own broader knowledge to explain, simplify, give
/// examples, and answer related questions the documents don't cover - see
/// the system instruction in AskAsync for the actual ground rules. Reuses
/// the same AI provider config as <see cref="DocumentCategorizationService"/>
/// (same appsettings.json "Gemini" section, same HTTP endpoint) rather than
/// a second AI integration.
/// </summary>
public sealed partial class TutorChatService(HttpClient httpClient, IOptions<DocumentCategorizationOptions> options)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly DocumentCategorizationOptions _options = options.Value;

    // Combined budget for all documents' text in one request, plus a
    // per-document cap so one large file can't crowd out every other
    // document. Sized conservatively (roughly 3,000 tokens) to leave
    // headroom in the model's context window for the prompt, the
    // question, and the answer. See BuildContext for how these are applied.
    private const int TotalContextCharBudget = 12_000;
    private const int PerDocumentCharBudget = 4_000;

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    /// <summary>
    /// Main entry point, called once per chat message. Builds a prompt from
    /// the course's document text, sends it to Gemini, and returns the
    /// answer plus which document it was drawn from (if any).
    /// </summary>
    public async Task<TutorChatResult> AskAsync(
        string question,
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return new TutorChatResult(
                "The AI tutor isn't configured yet - ask an administrator to set the DocumentCategorization API key in appsettings.json.",
                null);
        }

        // Only documents with successfully extracted text are useful here -
        // a document with none would just add noise to the prompt. Having
        // none at all is no longer a dead end (see BuildPrompt): the tutor
        // just answers from its own subject knowledge instead of refusing.
        var usableDocuments = documents.Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText)).ToList();
        var (context, wasTruncated) = usableDocuments.Count > 0
            ? BuildContext(usableDocuments)
            : (string.Empty, false);
        var prompt = BuildPrompt(question, context, wasTruncated);

        // Gemini's generateContent request shape: a system instruction
        // (the tutor's ground rules) plus one user turn (the prompt built above).
        var payload = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = "You are this student's course tutor - your job is to help them actually understand " +
                               "the subject, not just retrieve text. When course document excerpts are supplied, " +
                               "treat them as the source of truth for what this course specifically teaches and how " +
                               "it approaches the topic - ground your answer in them where they're relevant, and " +
                               "stay consistent with the course's own terminology and approach. But you are never " +
                               "limited to only what's in the excerpts: use your own broader subject knowledge " +
                               "freely to explain concepts clearly, fill in context the excerpts don't cover, and " +
                               "answer follow-up questions that are related to the topic but not mentioned in the " +
                               "documents at all. If a document doesn't cover the question, or none were supplied, " +
                               "answer from your own knowledge instead of saying you can't help. Never just copy or " +
                               "closely paraphrase sentences out of the excerpts - always teach the idea in your own " +
                               "words, the way a tutor explains something out loud, not the way a textbook reads. " +
                               "When the student asks you to simplify something, rephrase it, or give an example, " +
                               "always actually do it - never decline or say you're only able to use the documents. " +
                               "Stay focused on the subject the student is studying rather than drifting into " +
                               "unrelated territory. Format the answer for readability instead of one long " +
                               "paragraph: break it into short paragraphs separated by a blank line, and use \"- \" " +
                               "bullet points whenever you're listing multiple facts, steps, or examples. " +
                               "Return only valid JSON with keys \"answer\" and \"source\". \"answer\" is your reply " +
                               "as plain text (using \\n for line breaks). \"source\" is the exact filename of a " +
                               "supplied document your answer was substantially drawn from, or null if your answer " +
                               "mainly came from your own general knowledge rather than a specific document."
                    }
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
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Gemini's free tier returns a quotaId containing "PerDay" specifically
                // when the whole day's request allowance is used up (see
                // https://ai.google.dev/gemini-api/docs/rate-limits) - that's a wait
                // until the next daily reset, not something retrying in a few seconds
                // fixes, so it gets a message that says so instead of "try again".
                if (errorBody.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                {
                    return new TutorChatResult(
                        "The AI tutor has reached its daily question limit. It resets at midnight " +
                        "Pacific Time - please try again after that.",
                        null);
                }

                return new TutorChatResult(
                    "The AI tutor is being rate-limited right now. Please wait a moment and try again.",
                    null);
            }

            return new TutorChatResult($"The AI tutor request failed (HTTP {(int)response.StatusCode}). Please try again.", null);
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
                var parsed = ParseResponse(assistantText);
                if (parsed != null)
                {
                    return parsed;
                }

                // Model didn't follow the JSON contract; still show the raw
                // reply rather than a dead end, just without a cited source.
                return new TutorChatResult(assistantText.Trim(), null);
            }
        }

        return new TutorChatResult("The AI tutor didn't return a usable answer. Please try again.", null);
    }

    /// <summary>
    /// Concatenates each document's extracted text under a header naming its
    /// source file, so the AI can cite exactly which one it drew from. Each
    /// document is capped at <see cref="PerDocumentCharBudget"/> characters
    /// and the loop stops once <see cref="TotalContextCharBudget"/> is
    /// reached; any documents that don't fit are simply left out of that
    /// request rather than sent partially interleaved.
    /// </summary>
    private static (string Context, bool WasTruncated) BuildContext(IReadOnlyList<TutorSourceDocument> documents)
    {
        var builder = new StringBuilder();
        var wasTruncated = false;
        var remainingBudget = TotalContextCharBudget;

        foreach (var document in documents)
        {
            if (remainingBudget <= 0)
            {
                wasTruncated = true;
                break;
            }

            var text = document.ExtractedText!.Trim();
            var perDocumentLimit = Math.Min(PerDocumentCharBudget, remainingBudget);
            if (text.Length > perDocumentLimit)
            {
                text = text[..perDocumentLimit];
                wasTruncated = true;
            }

            builder.Append("=== Source: ").Append(document.SourceName).Append(" ===\n")
                   .Append(text).Append("\n\n");

            remainingBudget -= text.Length;
        }

        return (builder.ToString(), wasTruncated);
    }

    // Combines the document context and the student's question into the
    // single user-turn prompt text sent to Gemini. With no context at all
    // (no Coursework documents uploaded, or none with readable text), the
    // question is sent on its own - the system instruction already tells
    // the model to answer from its own knowledge in that case rather than
    // treating an empty excerpts section as "nothing to answer from".
    private static string BuildPrompt(string question, string context, bool wasTruncated)
    {
        if (string.IsNullOrEmpty(context))
        {
            return "No course documents have been uploaded for this course yet - answer from your own subject " +
                   "knowledge.\n\nStudent question:\n" + question.Trim();
        }

        var truncationNote = wasTruncated
            ? "\n(Note: the supplied excerpts were trimmed to fit a length limit and may not be complete.)\n"
            : string.Empty;

        return "Course document excerpts (what this course has actually taught so far):\n\n" + context + truncationNote +
               "\nStudent question:\n" + question.Trim();
    }

    // Parses the model's reply as { answer, source } JSON. Returns null
    // (not a thrown exception) for anything that doesn't fit, so AskAsync
    // can fall back to showing the raw reply instead of a dead end.
    private static TutorChatResult? ParseResponse(string aiResponse)
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

            if (!root.TryGetProperty("answer", out var answerElement))
            {
                return null;
            }

            var answer = answerElement.GetString();
            if (string.IsNullOrWhiteSpace(answer))
            {
                return null;
            }

            string? source = null;
            if (root.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String)
            {
                source = sourceElement.GetString();
            }

            return new TutorChatResult(answer.Trim(), string.IsNullOrWhiteSpace(source) ? null : source.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One document's extracted text, labeled by the name to cite it under.</summary>
public sealed record TutorSourceDocument(string SourceName, string? ExtractedText);

public sealed record TutorChatResult(string Answer, string? SourceDocument);
