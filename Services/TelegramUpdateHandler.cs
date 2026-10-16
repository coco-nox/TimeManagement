using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// Handles one update from Telegram (fetched by <see cref="TelegramBotService"/>):
/// "/start CODE" from the Details page's Connect link, button taps on a
/// session reminder, and replies that add a note to a cancelled session.
///
/// Who's who: Telegram only delivers updates to whoever holds the bot
/// token, so a tap can't be forged from outside - but it's still only acted
/// on when it comes from the chat linked to a FlightPath account, and only
/// for that account's own sessions. Every change goes through
/// <see cref="SessionReminderService"/>, the same rules as the in-app card.
///
/// Button callback data is "verb:taskId[:arg]" (Telegram allows 64 bytes):
/// m = push back arg minutes, c = ask why (show reasons), r = cancel with
/// reason index arg (-1 = none), b = back to the reminder, u = undo cancel,
/// ok = got it (dismiss).
/// </summary>
public sealed class TelegramUpdateHandler(
    ApplicationDbContext db,
    SessionReminderService reminderService,
    ITelegramClient telegram,
    ILogger<TelegramUpdateHandler> logger)
{
    private readonly ApplicationDbContext _db = db;
    private readonly SessionReminderService _reminderService = reminderService;
    private readonly ITelegramClient _telegram = telegram;
    private readonly ILogger<TelegramUpdateHandler> _logger = logger;

    public async Task HandleAsync(JsonElement update, DateTime now, CancellationToken cancellationToken = default)
    {
        if (update.TryGetProperty("callback_query", out var callback))
        {
            await HandleCallbackAsync(callback, now, cancellationToken);
        }
        else if (update.TryGetProperty("message", out var message))
        {
            await HandleMessageAsync(message, cancellationToken);
        }
    }

    // ---- button taps ------------------------------------------------------

    private async Task HandleCallbackAsync(JsonElement callback, DateTime now, CancellationToken cancellationToken)
    {
        var callbackId = callback.GetProperty("id").GetString()!;
        if (!callback.TryGetProperty("message", out var message) ||
            !callback.TryGetProperty("data", out var dataEl) || dataEl.GetString() is not { } data)
        {
            await _telegram.AnswerCallbackAsync(callbackId, null, cancellationToken: cancellationToken);
            return;
        }

        var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
        var messageId = message.GetProperty("message_id").GetInt64();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId, cancellationToken);
        if (user == null)
        {
            await _telegram.AnswerCallbackAsync(callbackId, "This chat isn't connected to FlightPath any more.", true, cancellationToken);
            return;
        }

        var parts = data.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var taskId))
        {
            await _telegram.AnswerCallbackAsync(callbackId, null, cancellationToken: cancellationToken);
            return;
        }
        var verb = parts[0];
        var arg = parts.Length > 2 && int.TryParse(parts[2], out var parsed) ? parsed : 0;

        var task = await _reminderService.FindAsync(user.Id, taskId);
        if (task == null)
        {
            await _telegram.EditMessageAsync(chatId, messageId, "This study session isn't on your Calendar any more.", null, cancellationToken);
            await _telegram.AnswerCallbackAsync(callbackId, "That session no longer exists.", cancellationToken: cancellationToken);
            return;
        }

        // Taps that arrive after the session is over (e.g. FlightPath was
        // off and is catching up) are refused rather than acted on late.
        var ended = task.ScheduledStart.AddMinutes(task.PlannedMinutes) <= now;
        if (ended && verb != "u")
        {
            await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.Ended(task), null, cancellationToken);
            await _telegram.AnswerCallbackAsync(callbackId, "This session is already over.", cancellationToken: cancellationToken);
            return;
        }

        switch (verb)
        {
            case "m":
                await PushBackAsync(user, task, arg, chatId, messageId, callbackId, now, cancellationToken);
                break;

            case "c":
                if (task.IsLogged)
                {
                    await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.AlreadyLogged(task), null, cancellationToken);
                    await _telegram.AnswerCallbackAsync(callbackId, "This session is already logged.", cancellationToken: cancellationToken);
                    break;
                }
                await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.AskReason(task), TelegramReminderMessages.ReasonButtons(task.Id), cancellationToken);
                await _telegram.AnswerCallbackAsync(callbackId, null, cancellationToken: cancellationToken);
                break;

            case "r":
                if (task.IsLogged)
                {
                    await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.AlreadyLogged(task), null, cancellationToken);
                    await _telegram.AnswerCallbackAsync(callbackId, "This session is already logged.", cancellationToken: cancellationToken);
                    break;
                }
                var reason = arg >= 0 && arg < TelegramReminderMessages.Reasons.Length ? TelegramReminderMessages.Reasons[arg] : null;
                await _reminderService.CancelAsync(task, reason, null);
                // Remember which message this is, so a reply to it adds a note.
                task.ReminderTelegramMessageId = messageId;
                await _db.SaveChangesAsync(cancellationToken);
                await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.Cancelled(task), TelegramReminderMessages.UndoButton(task.Id), cancellationToken);
                await _telegram.AnswerCallbackAsync(callbackId, "Session cancelled.", cancellationToken: cancellationToken);
                break;

            case "u":
                if (task.IsMissed && !ended)
                {
                    await _reminderService.UncancelAsync(task);
                    await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.Reminder(task, now), TelegramReminderMessages.ReminderButtons(task.Id), cancellationToken);
                    await _telegram.AnswerCallbackAsync(callbackId, "Session restored.", cancellationToken: cancellationToken);
                }
                else
                {
                    await _telegram.AnswerCallbackAsync(callbackId, ended ? "This session is already over." : "Nothing to undo.", cancellationToken: cancellationToken);
                }
                break;

            case "b":
                await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.Reminder(task, now), TelegramReminderMessages.ReminderButtons(task.Id), cancellationToken);
                await _telegram.AnswerCallbackAsync(callbackId, null, cancellationToken: cancellationToken);
                break;

            case "ok":
                await _reminderService.DismissAsync(task);
                await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.GotIt(task), null, cancellationToken);
                await _telegram.AnswerCallbackAsync(callbackId, "Have a good session!", cancellationToken: cancellationToken);
                break;

            default:
                await _telegram.AnswerCallbackAsync(callbackId, null, cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task PushBackAsync(ApplicationUser user, CalendarTask task, int minutes, long chatId, long messageId, string callbackId, DateTime now, CancellationToken cancellationToken)
    {
        if (!SessionReminderService.PushBackMinutes.Contains(minutes))
        {
            await _telegram.AnswerCallbackAsync(callbackId, null, cancellationToken: cancellationToken);
            return;
        }

        var error = await _reminderService.MoveAsync(task, task.ScheduledStart.AddMinutes(minutes), now);
        if (error != null)
        {
            await _telegram.AnswerCallbackAsync(callbackId, error, true, cancellationToken);
            return;
        }

        await _telegram.EditMessageAsync(chatId, messageId, TelegramReminderMessages.Moved(task, user.ReminderLeadMinutes), null, cancellationToken);
        await _telegram.AnswerCallbackAsync(callbackId, $"Moved to {task.ScheduledStart:h:mm tt}.", cancellationToken: cancellationToken);
    }

    // ---- messages -----------------------------------------------------------

    private async Task HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
        var text = message.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? string.Empty : string.Empty;

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var code = text.Length > 6 ? text[6..].Trim() : string.Empty;
            var username = message.TryGetProperty("from", out var from) && from.TryGetProperty("username", out var u) ? u.GetString() : null;
            await LinkAsync(chatId, code, username, cancellationToken);
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId, cancellationToken);
        if (user == null)
        {
            await _telegram.SendMessageAsync(chatId,
                "This chat isn't connected to FlightPath yet. Open FlightPath, go to <b>Profile › Details</b> and click <b>Connect Telegram</b>.", null, cancellationToken);
            return;
        }

        if (text.Equals("/disconnect", StringComparison.OrdinalIgnoreCase))
        {
            user.TelegramChatId = null;
            user.TelegramUsername = null;
            user.TelegramRemindersEnabled = false;
            await _db.SaveChangesAsync(cancellationToken);
            await _telegram.SendMessageAsync(chatId, "Disconnected. You won't get FlightPath reminders here any more.", null, cancellationToken);
            return;
        }

        // A reply to a cancelled-session message adds a note to its reason.
        if (message.TryGetProperty("reply_to_message", out var repliedTo) && repliedTo.ValueKind == JsonValueKind.Object &&
            repliedTo.TryGetProperty("message_id", out var repliedIdEl) && text.Length > 0)
        {
            var repliedId = repliedIdEl.GetInt64();
            var task = await _db.CalendarTasks.FirstOrDefaultAsync(
                x => x.UserId == user.Id && x.ReminderTelegramMessageId == repliedId, cancellationToken);

            if (task is { IsMissed: true })
            {
                var combined = string.IsNullOrEmpty(task.MissedReason) ? text : $"{task.MissedReason} - {text}";
                task.MissedReason = combined.Length > SessionReminderService.MaxReasonLength ? combined[..SessionReminderService.MaxReasonLength] : combined;
                await _db.SaveChangesAsync(cancellationToken);
                await _telegram.SendMessageAsync(chatId, "Note added - you'll see it on Analytics.", null, cancellationToken);
                return;
            }
        }

        await _telegram.SendMessageAsync(chatId,
            "I send your study-session reminders. Use the buttons on a reminder to push it back or cancel it, " +
            "or reply to a cancelled-session message to add a note. Send /disconnect to stop reminders here.", null, cancellationToken);
    }

    private async Task LinkAsync(long chatId, string code, string? username, CancellationToken cancellationToken)
    {
        if (code.Length == 0)
        {
            var existing = await _db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId, cancellationToken);
            await _telegram.SendMessageAsync(chatId, existing != null
                ? $"You're already connected to FlightPath as <b>{Html(existing.FullName)}</b>."
                : "Hi! To get study-session reminders here, open FlightPath, go to <b>Profile › Details</b> and click <b>Connect Telegram</b>.",
                null, cancellationToken);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.TelegramLinkCode == code && u.TelegramLinkCodeExpiresUtc > nowUtc, cancellationToken);
        if (user == null)
        {
            await _telegram.SendMessageAsync(chatId,
                "That connect link has expired or was already used. In FlightPath, go to <b>Profile › Details</b> and click <b>Connect Telegram</b> again.", null, cancellationToken);
            return;
        }

        // One chat belongs to one account: unlink it anywhere else first.
        var others = await _db.Users.Where(u => u.TelegramChatId == chatId && u.Id != user.Id).ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            other.TelegramChatId = null;
            other.TelegramUsername = null;
            other.TelegramRemindersEnabled = false;
        }

        user.TelegramChatId = chatId;
        user.TelegramUsername = username;
        user.TelegramRemindersEnabled = true;
        user.TelegramLinkCode = null;
        user.TelegramLinkCodeExpiresUtc = null;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Linked a Telegram chat to FlightPath user {UserId}.", user.Id);
        await _telegram.SendMessageAsync(chatId,
            $"Connected to FlightPath as <b>{Html(user.FullName)}</b>. Your study-session reminders will arrive here " +
            $"{user.ReminderLeadMinutes} minutes before each session, with buttons to push it back or cancel it.", null, cancellationToken);
    }

    private static string Html(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}

/// <summary>The texts and buttons of a Telegram session reminder, in each of
/// its states. Shared by the handler above and the reminder sender.</summary>
public static class TelegramReminderMessages
{
    /// <summary>Same reasons as the in-app card (Pages/Reminders).</summary>
    public static readonly string[] Reasons = TimeManagement.Pages.Reminders.IndexModel.CancelReasons;

    public static string Reminder(CalendarTask task, DateTime now)
    {
        var minutes = (int)Math.Round((task.ScheduledStart - now).TotalMinutes);
        var when = minutes > 0 ? $"Starts in {minutes} minute{(minutes == 1 ? "" : "s")}" : minutes == 0 ? "Starting now" : $"Started {-minutes} minute{(minutes == -1 ? "" : "s")} ago";
        return $"⏰ <b>{when}</b>\n\n{Details(task)}";
    }

    public static IReadOnlyList<IReadOnlyList<TelegramButton>> ReminderButtons(int taskId) =>
    [
        [new("+15 min", $"m:{taskId}:15"), new("+30 min", $"m:{taskId}:30")],
        [new("Cancel session", $"c:{taskId}"), new("Got it", $"ok:{taskId}")]
    ];

    public static string AskReason(CalendarTask task) =>
        $"<b>Why are you cancelling?</b>\nYour reason goes into your weekly review.\n\n{Details(task)}";

    public static IReadOnlyList<IReadOnlyList<TelegramButton>> ReasonButtons(int taskId)
    {
        var rows = Reasons
            .Select((reason, index) => new TelegramButton(reason, $"r:{taskId}:{index}"))
            .Chunk(2)
            .Select(chunk => (IReadOnlyList<TelegramButton>)chunk)
            .ToList();
        rows.Add([new("No reason", $"r:{taskId}:-1"), new("« Keep session", $"b:{taskId}")]);
        return rows;
    }

    public static string Cancelled(CalendarTask task) =>
        $"❌ <b>Session cancelled</b> - logged as missed on Analytics" +
        (string.IsNullOrEmpty(task.MissedReason) ? "." : $". Reason: {Html(task.MissedReason.ToLowerInvariant())}.") +
        $"\n\n{Details(task)}\n\n<i>Reply to this message to add a note.</i>";

    public static IReadOnlyList<IReadOnlyList<TelegramButton>> UndoButton(int taskId) => [[new("Undo", $"u:{taskId}")]];

    public static string Moved(CalendarTask task, int leadMinutes) =>
        $"⏩ <b>Moved to {task.ScheduledStart:h:mm tt}</b> - you'll get another reminder {leadMinutes} minutes before it starts.\n\n{Details(task)}";

    public static string GotIt(CalendarTask task) =>
        $"✅ <b>Have a good session!</b> Log how it went on Analytics afterwards.\n\n{Details(task)}";

    public static string Ended(CalendarTask task) =>
        $"This session is already over.\n\n{Details(task)}";

    public static string AlreadyLogged(CalendarTask task) =>
        $"This session is already logged as {(task.IsCompleted ? "done" : "missed")}.\n\n{Details(task)}";

    private static string Details(CalendarTask task)
    {
        var end = task.ScheduledStart.AddMinutes(task.PlannedMinutes);
        var course = task.Assessment?.Course?.Title;
        var line = $"<b>{Html(task.Title)}</b>";
        if (!string.IsNullOrEmpty(course))
        {
            line += $"\n{Html(course)}";
        }
        line += $"\n{task.ScheduledStart.ToString("ddd d MMM, h:mm", CultureInfo.CurrentCulture)} – {end:h:mm tt} · {task.PlannedMinutes} min";
        if (task.Assessment is { } assessment)
        {
            line += assessment.DueDate is DateTime due
                ? $"\nfor {Html(assessment.Title)}, due {due:ddd d MMM}"
                : $"\nfor {Html(assessment.Title)}";
        }
        return line;
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);
}
