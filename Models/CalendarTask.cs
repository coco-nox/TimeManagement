namespace TimeManagement.Models;

/// <summary>
/// A scheduled block of study/work time, optionally tied to an assessment.
/// Written by the Calendar page's AI scheduler; the student then logs how
/// each session went on the Analytics page (Done, with optional actual
/// minutes, or Missed). A session with neither flag set hasn't been logged yet.
/// </summary>
public class CalendarTask
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public int? AssessmentId { get; set; }

    public Assessment? Assessment { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime ScheduledStart { get; set; }

    public int PlannedMinutes { get; set; }

    /// <summary>How long a Done session actually took, if the student said -
    /// more than PlannedMinutes means it "ran over time". Null means they
    /// didn't say, which is treated as on time.</summary>
    public int? ActualMinutes { get; set; }

    public bool IsCompleted { get; set; }

    /// <summary>Logged as missed. Never true at the same time as IsCompleted.</summary>
    public bool IsMissed { get; set; }

    /// <summary>Why the session was missed, when the student cancelled it
    /// from the session reminder (e.g. "Too tired - lab ran late").</summary>
    public string? MissedReason { get; set; }

    /// <summary>When the in-app reminder was answered or closed, so it
    /// isn't shown again. Cleared when the session is pushed back or moved,
    /// so the new time gets its own reminder.</summary>
    public DateTime? ReminderDismissedUtc { get; set; }

    /// <summary>When the reminder email went out (see
    /// SessionReminderNotificationService) - set so it's only ever sent once
    /// per start time. Cleared on a move, same as ReminderDismissedUtc.</summary>
    public DateTime? ReminderEmailSentUtc { get; set; }

    /// <summary>When the Telegram reminder went out - same once-per-start-time
    /// rule as the email.</summary>
    public DateTime? ReminderTelegramSentUtc { get; set; }

    /// <summary>The Telegram message that reminder is, so its buttons can
    /// be updated in place ("Session cancelled") and a reply to it can add a
    /// note to the session.</summary>
    public long? ReminderTelegramMessageId { get; set; }

    /// <summary>True once the student has logged this session either way -
    /// the Calendar's "Generate schedule" keeps these rather than replacing
    /// them, so a regenerated plan never wipes the week's history.</summary>
    public bool IsLogged => IsCompleted || IsMissed;
}
