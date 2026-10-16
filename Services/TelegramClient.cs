using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace TimeManagement.Services;

/// <summary>
/// The handful of Telegram Bot API calls FlightPath uses. Every call goes
/// out from this app to api.telegram.org - nothing calls in - which is why
/// two-way Telegram reminders work while FlightPath only runs on the
/// student's own computer: <see cref="TelegramBotService"/> long-polls for
/// button taps and messages rather than waiting for Telegram to reach it.
///
/// The bot token is part of every request URL, so this client's HTTP
/// request logging is switched off in Program.cs (RemoveAllLoggers) - the
/// default logging would print the token into the console.
/// </summary>
public interface ITelegramClient
{
    bool IsConfigured { get; }

    /// <summary>The bot's @username (cached after the first call), or null
    /// when the token is missing or wrong.</summary>
    Task<string?> GetBotUsernameAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits up to <paramref name="timeoutSeconds"/> for new
    /// updates after <paramref name="offset"/>; null when the call failed.</summary>
    Task<List<JsonElement>?> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>Sends an HTML-formatted message; returns its message id, or null on failure.</summary>
    Task<long?> SendMessageAsync(long chatId, string html, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons, CancellationToken cancellationToken = default);

    /// <summary>Replaces a message's text and buttons (no buttons = remove them).</summary>
    Task<bool> EditMessageAsync(long chatId, long messageId, string html, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges a button tap; text shows as a toast (or a popup when <paramref name="alert"/>).</summary>
    Task AnswerCallbackAsync(string callbackQueryId, string? text, bool alert = false, CancellationToken cancellationToken = default);
}

/// <summary>One inline button. CallbackData (max 64 bytes) comes back in
/// the callback_query when it's tapped.</summary>
public sealed record TelegramButton(string Text, string CallbackData);

public sealed class TelegramOptions
{
    /// <summary>From @BotFather - User Secrets "Telegram:BotToken", never appsettings.json.</summary>
    public string BotToken { get; set; } = string.Empty;
}

public sealed class TelegramClient(HttpClient httpClient, IOptionsMonitor<TelegramOptions> options, ILogger<TelegramClient> logger) : ITelegramClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Shared across instances (this is a transient typed client) so the
    // Details page doesn't call getMe on every load. Keyed by token so a
    // changed token is picked up.
    private static (string Token, string Username)? _cachedBot;

    private readonly HttpClient _httpClient = httpClient;
    private readonly IOptionsMonitor<TelegramOptions> _options = options;
    private readonly ILogger<TelegramClient> _logger = logger;

    private string Token => _options.CurrentValue.BotToken?.Trim() ?? string.Empty;

    public bool IsConfigured => Token.Length > 0;

    public async Task<string?> GetBotUsernameAsync(CancellationToken cancellationToken = default)
    {
        var token = Token;
        if (token.Length == 0)
        {
            return null;
        }

        if (_cachedBot is { } cached && cached.Token == token)
        {
            return cached.Username;
        }

        var result = await CallAsync("getMe", new { }, cancellationToken);
        if (result is { } me && me.TryGetProperty("username", out var username) && username.GetString() is { } name)
        {
            _cachedBot = (token, name);
            return name;
        }

        return null;
    }

    public async Task<List<JsonElement>?> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync("getUpdates", new
        {
            offset,
            timeout = timeoutSeconds,
            allowed_updates = new[] { "message", "callback_query" }
        }, cancellationToken);

        return result is { ValueKind: JsonValueKind.Array } updates ? [.. updates.EnumerateArray()] : null;
    }

    public async Task<long?> SendMessageAsync(long chatId, string html, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync("sendMessage", new
        {
            chat_id = chatId,
            text = html,
            parse_mode = "HTML",
            reply_markup = buttons == null ? null : Keyboard(buttons)
        }, cancellationToken);

        return result is { } message && message.TryGetProperty("message_id", out var id) ? id.GetInt64() : null;
    }

    public async Task<bool> EditMessageAsync(long chatId, long messageId, string html, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync("editMessageText", new
        {
            chat_id = chatId,
            message_id = messageId,
            text = html,
            parse_mode = "HTML",
            reply_markup = Keyboard(buttons ?? [])
        }, cancellationToken);

        return result != null;
    }

    public async Task AnswerCallbackAsync(string callbackQueryId, string? text, bool alert = false, CancellationToken cancellationToken = default)
    {
        await CallAsync("answerCallbackQuery", new { callback_query_id = callbackQueryId, text, show_alert = alert }, cancellationToken);
    }

    private static object Keyboard(IReadOnlyList<IReadOnlyList<TelegramButton>> rows) => new
    {
        inline_keyboard = rows.Select(row => row.Select(b => new { text = b.Text, callback_data = b.CallbackData }))
    };

    private async Task<JsonElement?> CallAsync(string method, object payload, CancellationToken cancellationToken)
    {
        var token = Token;
        if (token.Length == 0)
        {
            return null;
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out var result))
            {
                return result.Clone();
            }

            // Telegram's own description ("Unauthorized", "message is not
            // modified", ...) - never the URL, which holds the token.
            var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
            if (description?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) != true)
            {
                _logger.LogWarning("Telegram {Method} failed (HTTP {StatusCode}): {Description}", method, (int)response.StatusCode, description);
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Telegram {Method} request failed: {Error}", method, ex.GetType().Name);
            return null;
        }
    }
}
