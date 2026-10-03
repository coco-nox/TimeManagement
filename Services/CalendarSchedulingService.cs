using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TimeManagement.Services;

/// <summary>
/// Turns a student's painted availability, study-hour preferences, and
/// upcoming assessment due dates into a proposed set of study sessions.
/// Reuses the same AI provider config as the other AI features (same
/// appsettings.json "Gemini" section, same HTTP endpoint) rather than a
/// separate integration. Deliberately instructed (and separately, server-
/// side capped by the caller - see Pages/Calendar/Index.cshtml.cs
/// OnPostGenerateScheduleAsync) to NOT fill every free hour just because
/// it's available: a schedule is only useful if a student could actually
/// follow it, which means realistic session lengths, sensible spacing
/// across the week, and stopping once an assessment has enough prep time
/// rather than padding the week to match the hours on offer.
/// </summary>
public sealed partial class CalendarSchedulingService(
    HttpClient httpClient,
    IOptions<DocumentCategorizationOptions> options,
    ILogger<CalendarSchedulingService> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<CalendarSchedulingService> _logger = logger;
    private readonly DocumentCategorizationOptions _options = options.Value;

    [GeneratedRegex("^```(json|JSON)?\\s*|\\s*```$", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRemover();

    public async Task<CalendarSchedulingResult> GenerateAsync(
        List<AvailableWindow> windows,
        List<EligibleAssessment> assessments,
        int totalWeeklyHours,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return new CalendarSchedulingResult(
                [],
                "The AI scheduler isn't configured yet - ask an administrator to set the DocumentCategorization API key in appsettings.json.");
        }

        if (windows.Count == 0 || assessments.Count == 0)
        {
            return new CalendarSchedulingResult([], null);
        }

        var prompt = BuildPrompt(windows, assessments, totalWeeklyHours);

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = "You are a study scheduling assistant building ONE student's realistic weekly study " +
                               "plan. Only ever propose a session inside the free time windows listed below - never " +
                               "a date, hour, or duration that falls outside a listed window. The student's total " +
                               "study time across the whole week must not exceed " + totalWeeklyHours + " hours, and " +
                               "time spent on any one course must not exceed that course's own weekly hour budget " +
                               "listed next to it. Do not fill every free hour just because it happens to be " +
                               "available - only schedule sessions that meaningfully help prepare for one of the " +
                               "listed assessments, and stop scheduling more for an assessment once it has " +
                               "reasonable prep time rather than padding the week to match the hours on offer. " +
                               "Prioritise assessments with closer due dates. Space sessions out sensibly across " +
                               "the available days instead of cramming them all into one day, and use realistic " +
                               "session lengths - normally 45 to 90 minutes, never more than 120. Give each session " +
                               "a short, concrete, actionable title describing what to actually do in that session " +
                               "(e.g. \"Draft outline for Final Report\", \"Practice past quiz questions\") - never " +
                               "a vague title like \"Study\" or \"Work on course\". It's fine, and often correct, to " +
                               "leave some listed windows empty or return fewer sessions than the hour budgets " +
                               "would allow if that's all a realistic plan needs. Return only valid JSON: an array " +
                               "of objects with keys \"date\" (string, yyyy-MM-dd, must be one of the listed dates), " +
                               "\"startHour\" (integer, must fall inside that date's listed window), " +
                               "\"durationMinutes\" (integer, the session must end at or before the end of that " +
                               "window), \"assessmentId\" (integer, must be one of the listed assessment ids), and " +
                               "\"title\" (string)."
                    }
                }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { temperature = 0.3 }
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
                    return new CalendarSchedulingResult(
                        [],
                        "The AI scheduler has reached its daily question limit. It resets at midnight " +
                        "Pacific Time - please try again after that.");
                }

                return new CalendarSchedulingResult([], "The AI scheduler is being rate-limited right now. Please wait a moment and try again.");
            }

            var failureBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Calendar scheduling request to Gemini failed with HTTP {StatusCode}. Response body: {Body}",
                (int)response.StatusCode, failureBody);

            return new CalendarSchedulingResult([], $"The scheduling request failed (HTTP {(int)response.StatusCode}). Please try again.");
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
                var sessions = ParseResponse(assistantText);
                if (sessions != null)
                {
                    // An empty array is a legitimate answer (see the "fine to
                    // leave windows empty" instruction above), not a failure.
                    return new CalendarSchedulingResult(sessions, null);
                }
            }
        }

        return new CalendarSchedulingResult([], "The AI scheduler didn't return a usable plan. Please try again.");
    }

    private static string BuildPrompt(List<AvailableWindow> windows, List<EligibleAssessment> assessments, int totalWeeklyHours)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"The student wants to study at most {totalWeeklyHours} hours this week.");
        builder.AppendLine();
        builder.AppendLine("Free time windows (only propose sessions inside these):");
        foreach (var window in windows)
        {
            builder.AppendLine($"- {window.Date:yyyy-MM-dd} ({window.Date:ddd}): {FormatHour(window.StartHour)}-{FormatHour(window.EndHour)}");
        }

        builder.AppendLine();
        builder.AppendLine("Assessments that may need study time, with each course's weekly hour budget:");
        foreach (var assessment in assessments)
        {
            var daysUntilDue = (assessment.DueDate.Date - DateTime.Today).Days;
            var dueDescription = daysUntilDue switch
            {
                < 0 => $"{-daysUntilDue} day(s) OVERDUE",
                0 => "due today",
                _ => $"due in {daysUntilDue} day(s)"
            };

            builder.AppendLine(
                $"- id {assessment.AssessmentId}: \"{assessment.AssessmentTitle}\" ({assessment.Category}) for " +
                $"{assessment.CourseTitle}, {dueDescription} ({assessment.DueDate:yyyy-MM-dd}). " +
                $"{assessment.CourseTitle}'s weekly budget: {assessment.CourseHoursPerWeek} hour(s).");
        }

        return builder.ToString();
    }

    private static string FormatHour(int hour)
    {
        var time = DateTime.Today.AddHours(hour);
        return time.ToString("h tt");
    }

    private static List<ScheduleSuggestion>? ParseResponse(string aiResponse)
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

            var sessions = new List<ScheduleSuggestion>();
            foreach (var element in root.EnumerateArray())
            {
                if (!element.TryGetProperty("date", out var dateEl) ||
                    !element.TryGetProperty("startHour", out var startHourEl) ||
                    !element.TryGetProperty("durationMinutes", out var durationEl) ||
                    !element.TryGetProperty("assessmentId", out var assessmentIdEl) ||
                    !element.TryGetProperty("title", out var titleEl))
                {
                    continue;
                }

                var dateText = dateEl.GetString();
                var title = titleEl.GetString();

                if (string.IsNullOrWhiteSpace(dateText) || string.IsNullOrWhiteSpace(title) ||
                    !DateTime.TryParse(dateText, out var date) ||
                    startHourEl.ValueKind != JsonValueKind.Number ||
                    durationEl.ValueKind != JsonValueKind.Number ||
                    assessmentIdEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                sessions.Add(new ScheduleSuggestion(
                    date.Date,
                    startHourEl.GetInt32(),
                    durationEl.GetInt32(),
                    assessmentIdEl.GetInt32(),
                    title.Trim()));
            }

            return sessions;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One contiguous block of free hours on one date, e.g. 6 PM-9 PM on
/// a Tuesday - built by grouping a day's consecutive Available (and not
/// already Scheduled) grid cells. EndHour is exclusive.</summary>
public sealed record AvailableWindow(DateTime Date, int StartHour, int EndHour);

/// <summary>One assessment the AI is allowed to propose sessions for, along
/// with the course it belongs to and that course's weekly hour budget from
/// CourseHoursPreference.</summary>
public sealed record EligibleAssessment(
    int AssessmentId,
    int CourseId,
    string CourseTitle,
    string AssessmentTitle,
    string Category,
    DateTime DueDate,
    int CourseHoursPerWeek);

/// <summary>One AI-proposed session, as returned before the caller validates
/// it against the actual available cells and hour caps.</summary>
public sealed record ScheduleSuggestion(DateTime Date, int StartHour, int DurationMinutes, int AssessmentId, string Title);

public sealed record CalendarSchedulingResult(List<ScheduleSuggestion> Sessions, string? Error);
