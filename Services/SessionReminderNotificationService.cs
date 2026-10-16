using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// Runs inside FlightPath for as long as the app is running, checking once
/// a minute for study sessions that are due a reminder and sending it by
/// email (through <see cref="EmailSender"/>, the same Gmail account as
/// password resets) and/or Telegram (with +15 min / Cancel buttons, which
/// <see cref="TelegramBotService"/> picks up) - whichever the student turned
/// on in Profile &gt; Details. That's what reaches the student's phone
/// without any FlightPath page being open.
///
/// Only sessions that haven't started yet are sent, so starting the app
/// after a gap never sends a burst of late reminders. Each channel is marked
/// sent (<see cref="CalendarTask.ReminderEmailSentUtc"/>,
/// <see cref="CalendarTask.ReminderTelegramSentUtc"/>) only once it really
/// went, so a failed send is retried on the next check until the session starts.
/// </summary>
public sealed class SessionReminderNotificationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SessionReminderNotificationService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<SessionReminderNotificationService> _logger = logger;
    private readonly string _appUrl = configuration["Reminders:AppBaseUrl"] ?? "http://localhost:5243";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await SendDueRemindersAsync(DateTime.Now, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad check (database locked, Gmail down) must never
                // stop the job for the rest of the app's run.
                _logger.LogError(ex, "Session reminder check failed; trying again in a minute.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One check: send every reminder due within its owner's lead
    /// time that hasn't gone out on that channel yet.</summary>
    public async Task SendDueRemindersAsync(DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<EmailSender>();
        var telegram = scope.ServiceProvider.GetRequiredService<ITelegramClient>();

        var longestLead = ApplicationUser.ReminderLeadOptions.Max();
        var candidates = await db.CalendarTasks
            .Include(t => t.User)
            .Include(t => t.Assessment).ThenInclude(a => a!.Course)
            .Where(t => !t.IsCompleted && !t.IsMissed
                && t.ReminderDismissedUtc == null
                && t.ScheduledStart > now && t.ScheduledStart <= now.AddMinutes(longestLead)
                && t.User != null
                && ((t.User.EmailRemindersEnabled && t.ReminderEmailSentUtc == null)
                    || (t.User.TelegramRemindersEnabled && t.User.TelegramChatId != null && t.ReminderTelegramSentUtc == null)))
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var task in candidates)
        {
            var user = task.User!;
            if (task.ScheduledStart > now.AddMinutes(user.ReminderLeadMinutes))
            {
                continue;
            }

            if (user.EmailRemindersEnabled && task.ReminderEmailSentUtc == null && !string.IsNullOrWhiteSpace(user.Email))
            {
                var minutesUntil = (int)Math.Ceiling((task.ScheduledStart - now).TotalMinutes);
                if (await emailSender.SendSessionReminderAsync(user.Email, BuildEmail(task, minutesUntil, _appUrl), cancellationToken))
                {
                    task.ReminderEmailSentUtc = DateTime.UtcNow;
                    changed = true;
                    _logger.LogInformation("Sent session reminder email for session {TaskId}.", task.Id);
                }
            }

            if (user.TelegramRemindersEnabled && user.TelegramChatId is long chatId && task.ReminderTelegramSentUtc == null && telegram.IsConfigured)
            {
                var messageId = await telegram.SendMessageAsync(
                    chatId,
                    TelegramReminderMessages.Reminder(task, now),
                    TelegramReminderMessages.ReminderButtons(task.Id),
                    cancellationToken);
                if (messageId != null)
                {
                    task.ReminderTelegramSentUtc = DateTime.UtcNow;
                    task.ReminderTelegramMessageId = messageId;
                    changed = true;
                    _logger.LogInformation("Sent session reminder to Telegram for session {TaskId}.", task.Id);
                }
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public static SessionReminderEmail BuildEmail(CalendarTask task, int minutesUntil, string appUrl)
    {
        var end = task.ScheduledStart.AddMinutes(task.PlannedMinutes);
        var day = task.ScheduledStart.Date == DateTime.Today ? "Today" : task.ScheduledStart.ToString("dddd d MMMM");
        var assessment = task.Assessment;

        return new SessionReminderEmail(
            task.Title,
            assessment?.Course?.Title,
            assessment?.Course?.ColourHex ?? FolderColours.DefaultHex,
            $"{day}, {task.ScheduledStart:h:mm} – {end:h:mm tt} · {task.PlannedMinutes} minutes",
            assessment == null
                ? null
                : assessment.DueDate is DateTime due ? $"For: {assessment.Title}, due {due:dddd d MMMM}" : $"For: {assessment.Title}",
            minutesUntil,
            appUrl);
    }
}
