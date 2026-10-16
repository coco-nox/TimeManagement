using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TimeManagement.Services;

/// <summary>
/// The one place in the app that talks to an AI provider. Every AI feature
/// calls <see cref="AskAsync{T}"/> with its <see cref="AiTask"/> instead of
/// calling Gemini or Groq itself; which provider handles each task comes
/// from the appsettings.json "AiRouting" section, so moving a task to the
/// other provider is a one-word config change (picked up without a restart).
///
/// If the routed provider fails - HTTP error, rate limit, timeout, or a reply
/// the caller's parse function rejects - the request is retried once on the
/// other provider (when it has a key) and the switch is logged. Keys live in
/// User Secrets as Gemini:ApiKey and Groq:ApiKey, never in appsettings.json.
/// </summary>
public sealed class AiClient(
    HttpClient httpClient,
    IOptionsMonitor<AiProviderOptions> providerOptions,
    IOptionsMonitor<AiRoutingOptions> routingOptions,
    ILogger<AiClient> logger)
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>True if at least one provider has an API key, i.e. an AI
    /// request has some chance of working.</summary>
    public bool IsConfigured => HasKey(AiProvider.Gemini) || HasKey(AiProvider.Groq);

    /// <summary>
    /// Sends <paramref name="request"/> to the provider routed for
    /// <paramref name="task"/>, falling back to the other provider once on
    /// failure. <paramref name="parse"/> turns the model's reply text into the
    /// caller's result; returning null marks the reply unusable, which counts
    /// as a failure (and so also triggers the fallback). Never throws for
    /// provider problems - check <see cref="AiResult{T}.Failure"/> instead.
    /// </summary>
    public async Task<AiResult<T>> AskAsync<T>(
        AiTask task,
        AiRequest request,
        Func<string, T?> parse,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var primary = GetProvider(task);
        var fallback = primary == AiProvider.Gemini ? AiProvider.Groq : AiProvider.Gemini;

        var first = await TryProviderAsync(task, primary, request, parse, cancellationToken);
        if (first.Failure == AiFailure.None || !HasKey(fallback))
        {
            return first;
        }

        logger.LogWarning(
            "AI task {Task}: {Primary} failed ({Failure}); switching to fallback provider {Fallback}.",
            task, primary, first.Failure, fallback);

        // When both fail, the fallback's failure is the one reported: it's
        // the most recent attempt, so its advice ("busy, try again in a
        // minute", "daily limit reached") is the one that actually applies now.
        var second = await TryProviderAsync(task, fallback, request, parse, cancellationToken);
        if (second.Failure != AiFailure.None)
        {
            logger.LogWarning(
                "AI task {Task}: fallback provider {Fallback} also failed ({Failure}).",
                task, fallback, second.Failure);
        }

        return second;
    }

    /// <summary>
    /// The student-facing message for a failed request. assistantName is
    /// e.g. "AI tutor", requestName e.g. "quiz" (as in "the quiz request
    /// failed"), resultName e.g. "quiz" (as in "didn't return a usable quiz").
    /// </summary>
    public static string DescribeFailure<T>(AiResult<T> result, string assistantName, string requestName, string resultName)
        where T : class => result.Failure switch
    {
        AiFailure.NotConfigured =>
            $"The {assistantName} isn't configured yet - ask an administrator to add a Gemini or Groq API key.",
        // Gemini's free tier resets its daily quota at midnight Pacific Time
        // (https://ai.google.dev/gemini-api/docs/rate-limits); Groq's limits roll.
        AiFailure.DailyLimitReached when result.Provider == AiProvider.Gemini =>
            $"The {assistantName} has reached its daily question limit. It resets at midnight Pacific Time - please try again after that.",
        AiFailure.DailyLimitReached =>
            $"The {assistantName} has reached its daily question limit. Please try again later.",
        AiFailure.RateLimited =>
            $"The {assistantName} is being rate-limited right now. Please wait a moment and try again.",
        AiFailure.Overloaded =>
            $"The {assistantName} is busy right now (the AI servers are overloaded). Please try again in a minute or two.",
        AiFailure.InvalidResponse =>
            $"The {assistantName} didn't return a usable {resultName}. Please try again.",
        _ when result.StatusCode is int statusCode =>
            $"The {requestName} request failed (HTTP {statusCode}). Please try again.",
        _ =>
            $"The {requestName} request failed. Please try again."
    };

    private AiProvider GetProvider(AiTask task) =>
        routingOptions.CurrentValue.TryGetValue(task, out var provider) ? provider : AiProvider.Gemini;

    private AiProviderOptions GetOptions(AiProvider provider) => providerOptions.Get(provider.ToString());

    private bool HasKey(AiProvider provider) => !string.IsNullOrWhiteSpace(GetOptions(provider).ApiKey);

    private async Task<AiResult<T>> TryProviderAsync<T>(
        AiTask task,
        AiProvider provider,
        AiRequest request,
        Func<string, T?> parse,
        CancellationToken cancellationToken)
        where T : class
    {
        var options = GetOptions(provider);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new AiResult<T>(null, provider, AiFailure.NotConfigured, null);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var httpRequest = provider == AiProvider.Gemini
                ? BuildGeminiRequest(options, request)
                : BuildGroqRequest(options, request);
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Logged (not shown to the student) so the real cause - a bad
                // model name, quota issue, or the provider being overloaded -
                // is visible in the server console.
                logger.LogWarning(
                    "AI task {Task}: {Provider} returned HTTP {StatusCode}. Response body: {Body}",
                    task, provider, (int)response.StatusCode, body);
                return new AiResult<T>(null, provider, ClassifyFailure(response.StatusCode, body), (int)response.StatusCode);
            }

            var text = provider == AiProvider.Gemini ? ReadGeminiText(body) : ReadGroqText(body);
            var value = string.IsNullOrWhiteSpace(text) ? null : parse(text);
            if (value == null)
            {
                logger.LogWarning(
                    "AI task {Task}: {Provider} returned a reply that couldn't be used. Reply: {Reply}",
                    task, provider, text);
                return new AiResult<T>(null, provider, AiFailure.InvalidResponse, null);
            }

            logger.LogInformation(
                "AI task {Task} handled by {Provider} ({Model}) in {ElapsedMs} ms.",
                task, provider, options.Model, stopwatch.ElapsedMilliseconds);
            return new AiResult<T>(value, provider, AiFailure.None, null);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException ||
            (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "AI task {Task}: request to {Provider} failed.", task, provider);
            return new AiResult<T>(null, provider, AiFailure.RequestFailed, null);
        }
    }

    // Gemini generateContent: a system instruction plus one user turn.
    private static HttpRequestMessage BuildGeminiRequest(AiProviderOptions options, AiRequest request)
    {
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = request.SystemInstruction } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = request.Prompt } } } },
            generationConfig = new
            {
                temperature = request.Temperature,
                responseMimeType = request.JsonOutput ? "application/json" : null
            }
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint}/{options.Model}:generateContent")
        {
            Content = JsonContent.Create(payload, options: RequestJsonOptions)
        };
        httpRequest.Headers.Add("x-goog-api-key", options.ApiKey);
        return httpRequest;
    }

    // Groq's OpenAI-compatible chat completions API. Its JSON mode only
    // allows a top-level object (not an array), so JSON prompts must ask for one.
    private static HttpRequestMessage BuildGroqRequest(AiProviderOptions options, AiRequest request)
    {
        var payload = new
        {
            model = options.Model,
            messages = new[]
            {
                new { role = "system", content = request.SystemInstruction },
                new { role = "user", content = request.Prompt }
            },
            temperature = request.Temperature,
            response_format = request.JsonOutput ? new { type = "json_object" } : null,
            reasoning_effort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? null : options.ReasoningEffort
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint}/chat/completions")
        {
            Content = JsonContent.Create(payload, options: RequestJsonOptions)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return httpRequest;
    }

    // candidates[0].content.parts[*].text, skipping any "thought" parts.
    private static string? ReadGeminiText(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0 ||
            !candidates[0].TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (part.TryGetProperty("text", out var partText))
            {
                text.Append(partText.GetString());
            }
        }

        return text.ToString();
    }

    // choices[0].message.content
    private static string? ReadGroqText(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.GetString();
    }

    private static AiFailure ClassifyFailure(HttpStatusCode statusCode, string body) => statusCode switch
    {
        // Gemini's daily quota has a quotaId containing "PerDay"; Groq's
        // daily limits are reported as "requests per day (RPD)" / "tokens per day (TPD)".
        HttpStatusCode.TooManyRequests when
            body.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("per day", StringComparison.OrdinalIgnoreCase) => AiFailure.DailyLimitReached,
        HttpStatusCode.TooManyRequests => AiFailure.RateLimited,
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => AiFailure.Overloaded,
        _ => AiFailure.RequestFailed
    };
}

/// <summary>Every AI feature in the app - the keys of the "AiRouting" config section.</summary>
public enum AiTask
{
    TutorChat,
    QuizGeneration,
    DocumentCategorization,
    ReportChecklist,
    CalendarScheduling,
    AnalyticsReport,
    StudyGuide
}

public enum AiProvider
{
    Gemini,
    Groq
}

public enum AiFailure
{
    None,
    NotConfigured,
    RateLimited,
    DailyLimitReached,
    Overloaded,
    InvalidResponse,
    RequestFailed
}

/// <summary>One AI request. JsonOutput turns on the provider's JSON mode -
/// the prompt must still describe the JSON shape it wants.</summary>
public sealed record AiRequest(string SystemInstruction, string Prompt, double Temperature, bool JsonOutput = false);

/// <summary>Value is set only when Failure is None. Provider is whichever
/// provider produced the result (or, on failure, the last one tried).</summary>
public sealed record AiResult<T>(T? Value, AiProvider? Provider, AiFailure Failure, int? StatusCode) where T : class;

/// <summary>One provider's settings - the "Gemini" and "Groq" sections of
/// appsettings.json (ApiKey always empty there; the real one is in User Secrets).</summary>
public sealed class AiProviderOptions
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL. Gemini appends "/{Model}:generateContent"; Groq appends "/chat/completions".</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Groq only, and only for reasoning models (e.g. gpt-oss): "low"
    /// keeps replies fast. Leave empty for non-reasoning models.</summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>Task -> provider mapping, bound from the "AiRouting" section.
/// A task missing from the section goes to Gemini.</summary>
public sealed class AiRoutingOptions : Dictionary<AiTask, AiProvider>
{
}
