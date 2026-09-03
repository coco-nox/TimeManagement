using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TimeManagement.Services;

/// <summary>
/// Answers a student's question about a course using only the extracted
/// text of that course's uploaded documents. Reuses the same AI provider
/// config as <see cref="DocumentCategorizationService"/> (same appsettings.json
/// "DocumentCategorization" section, same HTTP endpoint) rather than a
/// second AI integration.
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

        var usableDocuments = documents.Where(d => !string.IsNullOrWhiteSpace(d.ExtractedText)).ToList();
        if (usableDocuments.Count == 0)
        {
            return new TutorChatResult(
                "This course has no documents with readable text yet, so I don't have anything to answer from.",
                null);
        }

        var (context, wasTruncated) = BuildContext(usableDocuments);
        var prompt = BuildPrompt(question, context, wasTruncated);

        var payload = new
        {
            model = _options.Model,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a course tutor. Answer the student's question using ONLY the supplied " +
                              "document excerpts - never your own general knowledge, even if you know the answer. " +
                              "If the excerpts don't contain anything relevant, say so plainly instead of guessing. " +
                              "Return only valid JSON with keys \"answer\" and \"source\". \"answer\" is your reply " +
                              "as plain text. \"source\" is the exact source filename the answer was drawn from, " +
                              "or null if you found nothing relevant."
                },
                new { role = "user", content = prompt }
            },
            temperature = 0.1
        };

        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");

        using var response = await _httpClient.PostAsJsonAsync(_options.Endpoint, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new TutorChatResult($"The AI tutor request failed (HTTP {(int)response.StatusCode}). Please try again.", null);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonDocument.Parse(responseJson);
        var root = json.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
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

    private static string BuildPrompt(string question, string context, bool wasTruncated)
    {
        var truncationNote = wasTruncated
            ? "\n(Note: the supplied excerpts were trimmed to fit a length limit and may not be complete.)\n"
            : string.Empty;

        return "Course document excerpts:\n\n" + context + truncationNote +
               "\nStudent question:\n" + question.Trim();
    }

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
