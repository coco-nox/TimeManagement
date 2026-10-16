using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;

namespace TimeManagement.Services;

/// <summary>
/// The rules for timed study sessions (<see cref="StudySession"/>).
///
/// How time is counted: every open FlightPath page sends a heartbeat about
/// every 30 seconds saying whether the student did anything since the last
/// one. The time since the previous heartbeat (wall clock, so two open tabs
/// never double-count) is then:
/// - on a break: inactive;
/// - active: active - and any short pause before it (under 5 minutes:
///   thinking, reading the screen) is counted as active too;
/// - idle: offline study if the student recently said "working offline",
///   otherwise "pending" until they answer "Still studying?".
/// A gap with no heartbeat at all (every tab closed, computer asleep) is
/// idle time too. Pending time past 5 minutes asks "Still studying?":
/// "working offline" makes it offline study (and stops asking for 15
/// minutes); "took a break", or no answer within 2 minutes, makes it
/// inactive and starts a break. Pending time left when the session ends
/// counts as inactive if it's 5 minutes or more, active if it's less.
/// </summary>
public sealed class StudySessionService(ApplicationDbContext db)
{
    private readonly ApplicationDbContext _db = db;

    public const int CheckAfterIdleSeconds = 5 * 60;
    public const int AutoBreakAfterCheckSeconds = 2 * 60;
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromMinutes(15);

    /// <summary>A session nobody has touched for this long is ended for them.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

    /// <summary>The user's running session, if any (ending it first if it went stale).</summary>
    public async Task<StudySession?> GetActiveAsync(string userId, DateTime nowUtc)
    {
        var session = await _db.StudySessions
            .Include(s => s.Course)
            .FirstOrDefaultAsync(s => s.UserId == userId && s.EndedUtc == null);

        if (session != null && nowUtc - session.LastHeartbeatUtc > StaleAfter)
        {
            // Close it at its last sign of life, not now - the hours since
            // weren't a study session at all.
            Finish(session, session.LastHeartbeatUtc);
            await _db.SaveChangesAsync();
            return null;
        }

        return session;
    }

    /// <summary>One of the user's ended sessions (for saving its summary).</summary>
    public Task<StudySession?> FindEndedAsync(string userId, int sessionId) =>
        _db.StudySessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.EndedUtc != null);

    /// <summary>Starts a session (ending any already running). For a
    /// Calendar session, its reminder is dismissed too - the student has
    /// clearly started.</summary>
    public async Task<StudySession> StartAsync(string userId, int? calendarTaskId, int? courseId, DateTime nowUtc)
    {
        var running = await GetActiveAsync(userId, nowUtc);
        if (running != null)
        {
            Finish(running, nowUtc);
        }

        CalendarTask? task = null;
        if (calendarTaskId.HasValue)
        {
            task = await _db.CalendarTasks
                .Include(t => t.Assessment)
                .FirstOrDefaultAsync(t => t.Id == calendarTaskId.Value && t.UserId == userId);
        }
        else if (courseId.HasValue)
        {
            // "Start studying" in the Tutor: if one of this course's Calendar
            // sessions is due around now (within an hour either side) and
            // not yet logged, this is that session.
            var nowLocal = nowUtc.ToLocalTime();
            var from = nowLocal.AddHours(-1);
            var to = nowLocal.AddHours(1);
            var alreadyTimed = _db.StudySessions.Where(s => s.UserId == userId && s.CalendarTaskId != null).Select(s => s.CalendarTaskId);
            task = (await _db.CalendarTasks
                    .Include(t => t.Assessment)
                    .Where(t => t.UserId == userId && !t.IsCompleted && !t.IsMissed
                        && t.Assessment != null && t.Assessment.CourseId == courseId.Value
                        && t.ScheduledStart >= from && t.ScheduledStart <= to
                        && !alreadyTimed.Contains(t.Id))
                    .ToListAsync())
                .OrderBy(t => Math.Abs((t.ScheduledStart - nowLocal).TotalMinutes))
                .FirstOrDefault();
        }

        Course? course = null;
        var resolvedCourseId = task?.Assessment?.CourseId ?? courseId;
        if (resolvedCourseId.HasValue)
        {
            course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == resolvedCourseId.Value && c.UserId == userId);
        }

        if (task != null)
        {
            task.ReminderDismissedUtc ??= DateTime.UtcNow;
        }

        var session = new StudySession
        {
            UserId = userId,
            CalendarTaskId = task?.Id,
            CourseId = course?.Id,
            Title = task?.Title ?? (course != null ? $"Studying {course.Title}" : "Study session"),
            PlannedMinutes = task?.PlannedMinutes ?? 0,
            StartedUtc = nowUtc,
            LastHeartbeatUtc = nowUtc
        };
        _db.StudySessions.Add(session);
        await _db.SaveChangesAsync();
        session.Course = course;
        return session;
    }

    /// <summary>Applies one heartbeat (see the class summary).</summary>
    public async Task HeartbeatAsync(StudySession session, bool active, DateTime nowUtc)
    {
        Accrue(session, nowUtc, active);
        await _db.SaveChangesAsync();
    }

    /// <summary>The student's answer to "Still studying?".</summary>
    public async Task AnswerCheckAsync(StudySession session, bool workingOffline, DateTime nowUtc)
    {
        Accrue(session, nowUtc, active: false);
        if (workingOffline)
        {
            session.OfflineSeconds += session.PendingIdleSeconds;
            session.OfflineGraceUntilUtc = nowUtc + OfflineGrace;
        }
        else
        {
            session.InactiveSeconds += session.PendingIdleSeconds;
            session.IsOnBreak = true;
        }
        session.PendingIdleSeconds = 0;
        session.CheckShownUtc = null;
        await _db.SaveChangesAsync();
    }

    public async Task StartBreakAsync(StudySession session, DateTime nowUtc)
    {
        Accrue(session, nowUtc, active: false);
        // Away time right before the break joins it.
        session.InactiveSeconds += session.PendingIdleSeconds;
        session.PendingIdleSeconds = 0;
        session.CheckShownUtc = null;
        session.IsOnBreak = true;
        await _db.SaveChangesAsync();
    }

    public async Task EndBreakAsync(StudySession session, DateTime nowUtc)
    {
        Accrue(session, nowUtc, active: false);
        session.IsOnBreak = false;
        session.OfflineGraceUntilUtc = null;
        await _db.SaveChangesAsync();
    }

    public async Task EndAsync(StudySession session, DateTime nowUtc)
    {
        Finish(session, nowUtc);
        await _db.SaveChangesAsync();
    }

    /// <summary>"Save to Analytics" on the summary: optionally re-counts
    /// some inactive minutes as offline study ("Adjust times"), then logs
    /// the Calendar session, if any, as Done with the real time taken.</summary>
    public async Task SaveAsync(StudySession session, int minutesToCountAsOffline)
    {
        var moved = Math.Clamp(minutesToCountAsOffline * 60, 0, session.InactiveSeconds);
        session.InactiveSeconds -= moved;
        session.OfflineSeconds += moved;
        session.IsSaved = true;

        if (session.CalendarTaskId is int taskId)
        {
            var task = await _db.CalendarTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == session.UserId);
            if (task != null)
            {
                task.IsCompleted = true;
                task.IsMissed = false;
                task.MissedReason = null;
                task.ActualMinutes = Math.Max(1, (int)Math.Round(session.TotalSeconds / 60.0));
            }
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>Adds to the running session's counters (words written,
    /// checklist ticks, quiz answers, Tutor questions). No session, no-op.</summary>
    public async Task RecordAsync(string userId, Action<StudySession> update)
    {
        var session = await _db.StudySessions.FirstOrDefaultAsync(s => s.UserId == userId && s.EndedUtc == null);
        if (session == null)
        {
            return;
        }

        update(session);
        session.ChecklistTicked = Math.Max(0, session.ChecklistTicked);
        await _db.SaveChangesAsync();
    }

    /// <summary>Whether to show "Still studying?" right now.</summary>
    public static bool NeedsCheck(StudySession session) =>
        !session.IsOnBreak && session.PendingIdleSeconds >= CheckAfterIdleSeconds;

    /// <summary>
    /// The time rules, kept free of the database so they can be tested on
    /// their own. Moves the time since the last heartbeat into the right
    /// bucket and handles the 2-minute auto-break.
    /// </summary>
    public static void Accrue(StudySession session, DateTime nowUtc, bool active)
    {
        var elapsed = (int)Math.Max(0, (nowUtc - session.LastHeartbeatUtc).TotalSeconds);
        session.LastHeartbeatUtc = nowUtc;

        if (session.IsOnBreak)
        {
            session.InactiveSeconds += elapsed;
            return;
        }

        var inGrace = session.OfflineGraceUntilUtc is DateTime graceUntil && nowUtc <= graceUntil;

        if (active)
        {
            if (session.PendingIdleSeconds < CheckAfterIdleSeconds)
            {
                // A short pause, then back to work: all of it was studying.
                session.ActiveSeconds += session.PendingIdleSeconds + elapsed;
                session.PendingIdleSeconds = 0;
                session.CheckShownUtc = null;
            }
            else
            {
                // Back after a long gap: that gap waits for the answer to
                // "Still studying?"; the time now is active.
                session.ActiveSeconds += elapsed;
            }
        }
        else if (inGrace)
        {
            session.OfflineSeconds += elapsed;
        }
        else
        {
            session.PendingIdleSeconds += elapsed;
        }

        if (NeedsCheck(session))
        {
            session.CheckShownUtc ??= nowUtc;
            if ((nowUtc - session.CheckShownUtc.Value).TotalSeconds >= AutoBreakAfterCheckSeconds)
            {
                // Unanswered: counted as a break.
                session.InactiveSeconds += session.PendingIdleSeconds;
                session.PendingIdleSeconds = 0;
                session.CheckShownUtc = null;
                session.IsOnBreak = true;
            }
        }
    }

    /// <summary>Closes the session at <paramref name="endUtc"/>, settling any pending time.</summary>
    public static void Finish(StudySession session, DateTime endUtc)
    {
        if (endUtc > session.LastHeartbeatUtc)
        {
            Accrue(session, endUtc, active: false);
        }

        if (session.PendingIdleSeconds >= CheckAfterIdleSeconds)
        {
            session.InactiveSeconds += session.PendingIdleSeconds;
        }
        else
        {
            session.ActiveSeconds += session.PendingIdleSeconds;
        }

        session.PendingIdleSeconds = 0;
        session.CheckShownUtc = null;
        session.IsOnBreak = false;
        session.EndedUtc = endUtc;
    }
}
