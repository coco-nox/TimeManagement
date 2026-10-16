namespace TimeManagement.Models;

/// <summary>
/// One timed study session: started from a session reminder ("OK,
/// continue"), the Dashboard, or the Tutor, and ended with End session. While it
/// runs, every open FlightPath page sends a heartbeat saying whether the
/// student did anything (typed, scrolled, clicked) since the last one; see
/// <see cref="Services.StudySessionService"/> for how that becomes active,
/// offline and inactive time. Counters record what got done during it.
/// Only activity inside FlightPath can be seen - time away is "pending"
/// until the student says whether they were studying offline or on a break.
/// </summary>
public class StudySession
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>The scheduled Calendar session this is, if any (null =
    /// studying without one).</summary>
    public int? CalendarTaskId { get; set; }

    public CalendarTask? CalendarTask { get; set; }

    public int? CourseId { get; set; }

    public Course? Course { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Planned length, from the Calendar session (0 = open-ended).</summary>
    public int PlannedMinutes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? EndedUtc { get; set; }

    public DateTime LastHeartbeatUtc { get; set; }

    // ---- time, in seconds ---------------------------------------------------

    /// <summary>Doing something in FlightPath (short pauses included).</summary>
    public int ActiveSeconds { get; set; }

    /// <summary>Away from FlightPath, but the student said they were studying offline.</summary>
    public int OfflineSeconds { get; set; }

    /// <summary>Breaks, and away time the student didn't claim as studying.</summary>
    public int InactiveSeconds { get; set; }

    /// <summary>Away time not yet decided either way. Past 5 minutes it
    /// triggers the "Still studying?" question.</summary>
    public int PendingIdleSeconds { get; set; }

    /// <summary>After "Yes, working offline", away time until this counts
    /// as offline study without asking again.</summary>
    public DateTime? OfflineGraceUntilUtc { get; set; }

    /// <summary>When "Still studying?" was first shown for the current
    /// stretch of away time - unanswered for 2 minutes, it becomes a break.</summary>
    public DateTime? CheckShownUtc { get; set; }

    public bool IsOnBreak { get; set; }

    // ---- what got done --------------------------------------------------------

    public int WordsWritten { get; set; }

    public int ChecklistTicked { get; set; }

    public int QuizAnswered { get; set; }

    public int QuizCorrect { get; set; }

    public int TutorQuestions { get; set; }

    /// <summary>The end-of-session summary was saved to Analytics (the
    /// Calendar session, if any, is then logged Done with the real time).</summary>
    public bool IsSaved { get; set; }

    public int TotalSeconds => ActiveSeconds + OfflineSeconds + InactiveSeconds + PendingIdleSeconds;

    /// <summary>Time that counts as studying: in FlightPath or offline.</summary>
    public int StudiedSeconds => ActiveSeconds + OfflineSeconds;
}
