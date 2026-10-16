using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TimeManagement.Services;

/// <summary>
/// Writes a study guide for a Test or Quiz from the course's own documents:
/// key concepts, definitions, a short summary per topic, and practice
/// questions with answers (printed at the back so the student can test
/// themselves first). Sent through <see cref="AiClient"/> as
/// <see cref="AiTask.StudyGuide"/> - Gemini by default, since it reads long
/// documents well (see the appsettings.json "AiRouting" section).
/// </summary>
public sealed partial class StudyGuideService(AiClient aiClient)
{
    private readonly AiClient _aiClient = aiClient;

    // Bigger than the Tutor's budget: a guide should cover the whole unit.
    private const int TotalContextCharBudget = 24_000;
    private const int PerDocumentCharBudget = 8_000;
    private const int MaxTextLength = 600;

    private const string SystemInstruction =
        "You are an experienced tutor writing a printable study guide that helps one student revise for an " +
        "upcoming test or quiz. Use ONLY the supplied course document excerpts as the source of facts - never " +
        "invent facts, dates, formulas or examples that aren't supported by them. Write clearly and concisely, in " +
        "plain language a student can revise from. Cover the most important ideas across ALL the excerpts, not " +
        "just the first one. Return only valid JSON: an object with keys \"keyConcepts\" (array of 4 to 10 " +
        "strings, each one or two sentences), \"definitions\" (array of 3 to 12 objects with \"term\" and " +
        "\"meaning\", each meaning one sentence), \"topics\" (array of 2 to 6 objects with \"heading\" (short) " +
        "and \"summary\" (2 to 4 sentences)), and \"practiceQuestions\" (array of 5 to 10 objects with " +
        "\"question\" and \"answer\" - a mix of recall and understanding/application questions, answers one to " +
        "three sentences).";

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    public async Task<AiResult<StudyGuideContent>> GenerateAsync(
        string courseTitle,
        string assessmentTitle,
        string category,
        DateTime? dueDate,
        IReadOnlyList<TutorSourceDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var prompt = new StringBuilder()
            .AppendLine($"Course: {courseTitle}")
            .AppendLine($"Revising for: {assessmentTitle} ({category}){(dueDate is DateTime due ? $", on {due:dddd d MMMM}" : "")}")
            .AppendLine()
            .AppendLine("Course document excerpts:")
            .AppendLine()
            .Append(BuildContext(documents))
            .ToString();

        var request = new AiRequest(SystemInstruction, prompt, Temperature: 0.3, JsonOutput: true);
        return await _aiClient.AskAsync(AiTask.StudyGuide, request, Parse, cancellationToken);
    }

    private static string BuildContext(IReadOnlyList<TutorSourceDocument> documents)
    {
        var builder = new StringBuilder();
        var remaining = TotalContextCharBudget;
        foreach (var document in documents)
        {
            if (remaining <= 0)
            {
                break;
            }

            var text = document.ExtractedText!.Trim();
            var limit = Math.Min(PerDocumentCharBudget, remaining);
            if (text.Length > limit)
            {
                text = text[..limit];
            }

            builder.Append("=== Source: ").Append(document.SourceName).Append(" ===\n").Append(text).Append("\n\n");
            remaining -= text.Length;
        }
        return builder.ToString();
    }

    /// <summary>Validates the reply; also used to read a saved guide back.
    /// Null for anything unusable (too few concepts or questions).</summary>
    public static StudyGuideContent? Parse(string reply)
    {
        var text = reply.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = JsonFenceRemover().Replace(text, string.Empty);
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var concepts = Strings(root, "keyConcepts");
            var definitions = Pairs(root, "definitions", "term", "meaning");
            var topics = Pairs(root, "topics", "heading", "summary");
            var questions = Pairs(root, "practiceQuestions", "question", "answer");

            if (concepts.Count < 2 || questions.Count < 2)
            {
                return null;
            }

            return new StudyGuideContent(
                concepts,
                [.. definitions.Select(p => new GuideDefinition(p.A, p.B))],
                [.. topics.Select(p => new GuideTopic(p.A, p.B))],
                [.. questions.Select(p => new GuideQuestion(p.A, p.B))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(StudyGuideContent content) => JsonSerializer.Serialize(new
    {
        keyConcepts = content.KeyConcepts,
        definitions = content.Definitions.Select(d => new { term = d.Term, meaning = d.Meaning }),
        topics = content.Topics.Select(t => new { heading = t.Heading, summary = t.Summary }),
        practiceQuestions = content.PracticeQuestions.Select(q => new { question = q.Question, answer = q.Answer })
    });

    private static List<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0)
                .Select(Truncate)
                .Take(12)]
            : [];

    private static List<(string A, string B)> Pairs(JsonElement root, string name, string first, string second) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty(first, out var a) && a.ValueKind == JsonValueKind.String
                    && e.TryGetProperty(second, out var b) && b.ValueKind == JsonValueKind.String)
                .Select(e => (Truncate(e.GetProperty(first).GetString()!.Trim()), Truncate(e.GetProperty(second).GetString()!.Trim())))
                .Where(p => p.Item1.Length > 0 && p.Item2.Length > 0)
                .Take(12)]
            : [];

    private static string Truncate(string text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text;
}

public sealed record StudyGuideContent(
    List<string> KeyConcepts,
    List<GuideDefinition> Definitions,
    List<GuideTopic> Topics,
    List<GuideQuestion> PracticeQuestions);

public sealed record GuideDefinition(string Term, string Meaning);

public sealed record GuideTopic(string Heading, string Summary);

public sealed record GuideQuestion(string Question, string Answer);

/// <summary>Renders a study guide as simple, already-encoded HTML - one
/// rendering shared by the Study guide tab, the print/PDF page and the Word
/// download, so all three always match.</summary>
public static class StudyGuideHtml
{
    public static string Render(StudyGuideContent guide)
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder();

        html.Append("<h2>1. Key concepts</h2><ul>");
        foreach (var concept in guide.KeyConcepts)
        {
            html.Append("<li>").Append(E(concept)).Append("</li>");
        }
        html.Append("</ul>");

        var section = 2;
        if (guide.Definitions.Count > 0)
        {
            html.Append($"<h2>{section++}. Definitions</h2><table><tbody>");
            foreach (var definition in guide.Definitions)
            {
                html.Append("<tr><th>").Append(E(definition.Term)).Append("</th><td>").Append(E(definition.Meaning)).Append("</td></tr>");
            }
            html.Append("</tbody></table>");
        }

        if (guide.Topics.Count > 0)
        {
            html.Append($"<h2>{section++}. Topic summaries</h2>");
            foreach (var topic in guide.Topics)
            {
                html.Append("<h3>").Append(E(topic.Heading)).Append("</h3><p>").Append(E(topic.Summary)).Append("</p>");
            }
        }

        html.Append($"<h2>{section}. Practice questions</h2><ol>");
        foreach (var question in guide.PracticeQuestions)
        {
            html.Append("<li>").Append(E(question.Question)).Append("</li>");
        }
        html.Append("</ol><p><em>Answers are on the last page, so you can test yourself first.</em></p>");

        html.Append("<h2 class=\"guide-answers\">Answers</h2><ol>");
        foreach (var question in guide.PracticeQuestions)
        {
            html.Append("<li>").Append(E(question.Answer)).Append("</li>");
        }
        html.Append("</ol>");

        return html.ToString();
    }
}
