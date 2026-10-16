using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// Writes the Analytics page's "Your week, summarised" card: two or three
/// short observations about how the week actually went plus one concrete
/// suggestion for next week, from the week's logged sessions and check-in.
/// Sent through <see cref="AiClient"/> as <see cref="AiTask.AnalyticsReport"/>
/// - see the appsettings.json "AiRouting" section for which provider handles it.
///
/// Generated automatically (the page requests it after loading, so the
/// rest of the page never waits on the AI), and saved as a
/// <see cref="WeeklySummary"/> row. The saved summary is reused until the
/// facts it was written from change - see <see cref="WeeklySummary.InputHash"/>.
/// </summary>
public sealed partial class WeeklySummaryService(ApplicationDbContext db, AiClient aiClient)
{
    private readonly ApplicationDbContext _db = db;
    private readonly AiClient _aiClient = aiClient;

    // Bump when the prompt changes, so every saved summary is rewritten
    // under the new instructions rather than only those whose facts changed.
    private const string PromptVersion = "2";

    private const int MaxObservations = 3;
    private const int MaxTextLength = 300;

    private const string SystemInstruction =
        "You are a supportive but honest study coach writing a short end-of-week review for one student, " +
        "speaking directly to them as \"you\". Use ONLY the facts supplied - never invent numbers, sessions, " +
        "courses or feelings that aren't listed. Focus on what actually happened (sessions completed, missed, " +
        "or that ran over time, how much of each timed session was really spent studying versus inactive, and how " +
        "the student says the week felt), not just on what got finished. If " +
        "some sessions haven't been logged yet, don't treat them as missed. Be warm without being gushing, and " +
        "name a real pattern when the facts show one (e.g. the same day keeps slipping, sessions keep running " +
        "long, the student feels behind with a deadline close). Return only valid JSON: an object with keys " +
        "\"observations\" (array of 2 or 3 strings, each one plain sentence under 30 words) and \"suggestion\" " +
        "(string, one concrete, specific thing to try next week, under 35 words).";

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    // A sentence end (". ", "! ", "? ") followed by the next sentence's capital letter.
    [GeneratedRegex("(?<=[.!?])\\s+(?=[A-Z])")]
    private static partial Regex SentenceSplitter();

    /// <summary>
    /// Returns the week's summary - the saved one if its facts haven't
    /// changed, otherwise a freshly written (and saved) one. A week with
    /// nothing logged and no check-in gets a prompt to log something
    /// instead of an AI call about nothing.
    /// </summary>
    public async Task<WeeklySummaryResult> GetOrCreateAsync(string userId, WeekReviewFacts facts, CancellationToken cancellationToken = default)
    {
        if (facts.SessionsCompleted + facts.SessionsMissed == 0 && facts.CheckIn == null && !facts.TimedSessions.Any(s => !s.IsRunning))
        {
            return new WeeklySummaryResult(null,
                "Log how a few of this week's sessions went, or fill in the check-in below, and a summary will appear here.");
        }

        var prompt = BuildPrompt(facts);
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PromptVersion + "\n" + prompt)));

        var saved = await _db.WeeklySummaries
            .FirstOrDefaultAsync(s => s.UserId == userId && s.WeekStart == facts.WeekStart, cancellationToken);
        if (saved != null && saved.InputHash == inputHash && ParseResponse(saved.ContentJson) is { } savedContent)
        {
            return new WeeklySummaryResult(savedContent, null);
        }

        var request = new AiRequest(SystemInstruction, prompt, Temperature: 0.4, JsonOutput: true);
        var result = await _aiClient.AskAsync(AiTask.AnalyticsReport, request, ParseResponse, cancellationToken);
        if (result.Value is not { } content)
        {
            return new WeeklySummaryResult(null, AiClient.DescribeFailure(result, "AI coach", "summary", "summary"));
        }

        saved ??= _db.WeeklySummaries.Add(new WeeklySummary { UserId = userId, WeekStart = facts.WeekStart }).Entity;
        saved.InputHash = inputHash;
        saved.ContentJson = JsonSerializer.Serialize(new { observations = content.Observations, suggestion = content.Suggestion });
        saved.GeneratedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return new WeeklySummaryResult(content, null);
    }

    // Plain facts only, and nothing that changes from day to day on its own
    // (no "today", no "due in N days") - the prompt doubles as the cache
    // key, so it should only change when the student's data does.
    private static string BuildPrompt(WeekReviewFacts facts)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Week: {facts.WeekStart:ddd d MMM} to {facts.WeekStart.AddDays(6):ddd d MMM yyyy}");
        builder.AppendLine();
        builder.AppendLine("Study sessions this week:");
        builder.AppendLine($"- Scheduled: {facts.SessionsScheduled}");
        builder.AppendLine($"- Completed: {facts.SessionsCompleted}");
        builder.AppendLine($"- Missed: {facts.SessionsMissed}");
        builder.AppendLine($"- Not logged yet: {facts.SessionsUnlogged}");
        builder.AppendLine($"- Completed last week: {facts.PreviousWeekCompleted} of {facts.PreviousWeekScheduled} scheduled");

        foreach (var day in facts.Days.Where(d => d.Scheduled > 0))
        {
            builder.Append($"- {day.Date:dddd}: {day.Completed} completed, {day.Missed} missed of {day.Scheduled}");
            if (day.MissedTitles.Count > 0)
            {
                builder.Append($" (missed: {string.Join("; ", day.MissedTitles)})");
            }
            builder.AppendLine();
        }

        if (facts.Overruns.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Sessions that ran over their planned time:");
            foreach (var overrun in facts.Overruns)
            {
                builder.AppendLine($"- \"{overrun.Title}\" on {overrun.Date:dddd}: planned {overrun.PlannedMinutes} min, took {overrun.ActualMinutes} min");
            }
        }

        // Real time from the session timer (only sessions the student timed;
        // a running one is left out until it ends, so its numbers settle).
        var timed = facts.TimedSessions.Where(s => !s.IsRunning).ToList();
        if (timed.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Timed study sessions (actual time, from the session timer):");
            foreach (var s in timed)
            {
                builder.Append($"- \"{s.Title}\" {s.StartLocal:dddd h tt}: {s.TotalMinutes} min total");
                if (s.PlannedMinutes > 0) builder.Append($" (planned {s.PlannedMinutes})");
                builder.Append($", {s.StudiedMinutes} studied ({s.OfflineMinutes} of them offline), {s.InactiveMinutes} inactive");
                if (s.Achievements is { } done) builder.Append($"; {done.ToLowerInvariant()}");
                builder.AppendLine();
            }
        }

        if (facts.UpcomingDeadlines.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Coming up (not completed yet):");
            foreach (var deadline in facts.UpcomingDeadlines)
            {
                builder.AppendLine($"- {deadline}");
            }
        }

        builder.AppendLine();
        if (facts.CheckIn is { } checkIn)
        {
            builder.AppendLine("The student's own check-in for this week:");
            builder.AppendLine($"- Overall mood: {checkIn.Mood}");
            builder.AppendLine($"- Compared to last week: {checkIn.ComparedToLastWeek}");
            builder.AppendLine($"- Did the schedule feel right: {checkIn.ScheduleFit}");
            builder.AppendLine($"- How prepared they feel for what's coming: {checkIn.Preparedness}");
            if (!string.IsNullOrWhiteSpace(checkIn.Feedback))
            {
                builder.AppendLine($"- What they'd change next week: {checkIn.Feedback.Trim()}");
            }
        }
        else
        {
            builder.AppendLine("The student hasn't filled in this week's check-in yet.");
        }

        return builder.ToString();
    }

    // Validates the { observations, suggestion } JSON. Returns null for
    // anything else, which AiClient treats as an unusable reply.
    private static WeeklySummaryContent? ParseResponse(string aiResponse)
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
                !root.TryGetProperty("observations", out var observationsEl) || observationsEl.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("suggestion", out var suggestionEl) || suggestionEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            // Models sometimes pack every observation into one string; split
            // that back into one sentence per bullet so the card reads as a list.
            var observations = observationsEl.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.String)
                .Select(o => o.GetString()?.Trim())
                .Where(o => !string.IsNullOrEmpty(o))
                .SelectMany(o => observationsEl.GetArrayLength() == 1 ? SentenceSplitter().Split(o!) : [o!])
                .Select(o => o.Trim())
                .Where(o => o.Length > 0)
                .Select(Truncate)
                .Take(MaxObservations)
                .ToList();
            var suggestion = suggestionEl.GetString()?.Trim();

            if (observations.Count == 0 || string.IsNullOrEmpty(suggestion))
            {
                return null;
            }

            return new WeeklySummaryContent(observations, Truncate(suggestion));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text;
}

/// <summary>Everything the weekly summary is written from - built by the
/// Analytics page from the same numbers it shows.</summary>
public sealed record WeekReviewFacts(
    DateTime WeekStart,
    int SessionsScheduled,
    int SessionsCompleted,
    int SessionsMissed,
    int SessionsUnlogged,
    int PreviousWeekCompleted,
    int PreviousWeekScheduled,
    IReadOnlyList<WeekReviewDay> Days,
    IReadOnlyList<WeekReviewOverrun> Overruns,
    IReadOnlyList<string> UpcomingDeadlines,
    WeeklyCheckIn? CheckIn,
    IReadOnlyList<WeekReviewTimedSession> TimedSessions);

/// <summary>One study session timed with the session timer: real minutes
/// studied (in FlightPath + offline), inactive, and what got done.</summary>
public sealed record WeekReviewTimedSession(
    string Title,
    DateTime StartLocal,
    int PlannedMinutes,
    int ActiveMinutes,
    int OfflineMinutes,
    int InactiveMinutes,
    int WordsWritten,
    int ChecklistTicked,
    int QuizAnswered,
    int QuizCorrect,
    int TutorQuestions,
    bool IsRunning)
{
    public int StudiedMinutes => ActiveMinutes + OfflineMinutes;

    public int TotalMinutes => ActiveMinutes + OfflineMinutes + InactiveMinutes;

    /// <summary>"Wrote 640 words · ticked 2 checklist items · …", or null if nothing was recorded.</summary>
    public string? Achievements
    {
        get
        {
            var parts = new List<string>();
            if (WordsWritten > 0) parts.Add($"wrote {WordsWritten} words");
            if (ChecklistTicked > 0) parts.Add($"ticked {ChecklistTicked} checklist item{(ChecklistTicked == 1 ? "" : "s")}");
            if (QuizAnswered > 0) parts.Add($"{QuizAnswered} quiz question{(QuizAnswered == 1 ? "" : "s")} ({QuizCorrect} right)");
            if (TutorQuestions > 0) parts.Add($"asked the Tutor {TutorQuestions} question{(TutorQuestions == 1 ? "" : "s")}");
            if (parts.Count == 0) return null;
            var text = string.Join(" · ", parts);
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }
}

public sealed record WeekReviewDay(DateTime Date, int Scheduled, int Completed, int Missed, IReadOnlyList<string> MissedTitles);

public sealed record WeekReviewOverrun(string Title, DateTime Date, int PlannedMinutes, int ActualMinutes)
{
    public int MinutesOver => ActualMinutes - PlannedMinutes;
}

public sealed record WeeklySummaryContent(List<string> Observations, string Suggestion);

/// <summary>Content on success; otherwise Message says why there's no summary.</summary>
public sealed record WeeklySummaryResult(WeeklySummaryContent? Content, string? Message);
