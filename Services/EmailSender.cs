using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace TimeManagement.Services;

/// <summary>
/// Sends transactional email (password reset links and study-session
/// reminders) via Gmail's SMTP server using MailKit. Address/AppPassword come from
/// configuration section "Email" - always user-secrets locally
/// (dotnet user-secrets set Email:Address ... / Email:AppPassword ...),
/// never appsettings.json, since an app password is a real credential.
/// </summary>
public sealed class EmailSender(IOptions<EmailOptions> options, ILogger<EmailSender> logger)
{
    private readonly EmailOptions _options = options.Value;
    private readonly ILogger<EmailSender> _logger = logger;

    public async Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Address) || string.IsNullOrWhiteSpace(_options.AppPassword))
        {
            // Mirrors how the Gemini integrations degrade when unconfigured
            // (see DocumentCategorizationService) rather than throwing -
            // ForgotPasswordModel always shows the same message regardless
            // of whether this actually sent, so a missing config here
            // should never surface as an error to the student.
            _logger.LogWarning(
                "Password reset email to {Email} was not sent - Email:Address/Email:AppPassword aren't configured.",
                toEmail);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.Address));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Reset your FlightPath password";
        message.Body = new TextPart("plain")
        {
            Text =
                "We received a request to reset your FlightPath password.\n\n" +
                $"Reset it here: {resetLink}\n\n" +
                "If you didn't request this, you can safely ignore this email - your password won't change."
        };

        // Same reasoning as the missing-config case above: a failure is
        // logged, not thrown, so a Gmail-side hiccup can't reveal anything
        // to the caller or break the "always show the same message" flow.
        await TrySendAsync(message, "password reset", toEmail, cancellationToken);
    }

    /// <summary>
    /// Emails a "your session starts in N minutes" reminder. It informs
    /// rather than acts: FlightPath runs on the student's own computer, so a
    /// link tapped on a phone can't reach it - the email points them at the
    /// in-app reminder, which is where they push back, continue, or cancel.
    /// Returns whether Gmail accepted it, so the caller only marks the
    /// session as emailed when it really was.
    /// </summary>
    public async Task<bool> SendSessionReminderAsync(string toEmail, SessionReminderEmail reminder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Address) || string.IsNullOrWhiteSpace(_options.AppPassword))
        {
            _logger.LogWarning(
                "Session reminder email to {Email} was not sent - Email:Address/Email:AppPassword aren't configured.",
                toEmail);
            return false;
        }

        var startsIn = reminder.MinutesUntilStart <= 0 ? "is starting now" : $"starts in {reminder.MinutesUntilStart} minutes";
        var course = string.IsNullOrEmpty(reminder.CourseTitle) ? "" : $" ({reminder.CourseTitle})";
        var forLine = string.IsNullOrEmpty(reminder.ForLabel) ? "" : $"\n{reminder.ForLabel}";
        Func<string?, string?> encode = System.Net.WebUtility.HtmlEncode;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("FlightPath", _options.Address));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = $"{reminder.Title}{course} {startsIn}";

        var body = new BodyBuilder
        {
            TextBody =
                $"Your study session {startsIn}.\n\n" +
                $"{reminder.Title}{course}\n{reminder.TimeLabel}{forLine}\n\n" +
                $"Open FlightPath: {reminder.AppUrl}\n" +
                "To start, push it back, or cancel with a reason, use the reminder that pops up in FlightPath on your computer.\n\n" +
                "You're getting this because session reminders are turned on. Change how early they arrive, or turn email reminders off, in FlightPath under Profile > Details.",
            HtmlBody = $""""
                <div style="font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; color: #3d3731; max-width: 560px;">
                  <p style="font-size: 20px; font-weight: 700; margin: 0 0 20px;">FlightPath</p>
                  <div style="background-color: #f2ede2; border: 1px solid #cbb093; border-radius: 12px; padding: 22px 24px;">
                    <p style="font-size: 13px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; color: #6b5940; margin: 0 0 10px;">Your study session {encode(startsIn)}</p>
                    {(string.IsNullOrEmpty(reminder.CourseTitle) ? "" : $"""<p style="font-size: 15px; color: #6b5940; margin: 0 0 4px;"><span style="display: inline-block; width: 10px; height: 10px; border-radius: 50%; background-color: {encode(reminder.CourseColourHex)};"></span>&nbsp; {encode(reminder.CourseTitle)}</p>""")}
                    <p style="font-size: 24px; font-weight: 700; margin: 0 0 6px;">{encode(reminder.Title)}</p>
                    <p style="font-size: 16px; color: #6b5940; margin: 0;">{encode(reminder.TimeLabel)}</p>
                    {(string.IsNullOrEmpty(reminder.ForLabel) ? "" : $"""<p style="font-size: 16px; color: #6b5940; margin: 4px 0 0;">{encode(reminder.ForLabel)}</p>""")}
                  </div>
                  <p style="margin: 22px 0 10px;"><a href="{encode(reminder.AppUrl)}" style="display: inline-block; background-color: #5c6f5c; color: #ffffff; font-size: 16px; font-weight: 600; text-decoration: none; padding: 13px 26px; border-radius: 8px;">Open FlightPath</a></p>
                  <p style="font-size: 15px; line-height: 1.5; color: #6b5940; margin: 0 0 20px;">To start, push it back, or cancel with a reason, use the reminder that pops up in FlightPath on your computer.</p>
                  <p style="border-top: 1px solid #e0d6c4; padding-top: 14px; font-size: 13px; line-height: 1.5; color: #6b5940; margin: 0;">You're getting this because session reminders are turned on. Change how early they arrive, or turn email reminders off, in FlightPath under Profile &rsaquo; Details.</p>
                </div>
                """"
        };
        message.Body = body.ToMessageBody();

        return await TrySendAsync(message, "session reminder", toEmail, cancellationToken);
    }

    private async Task<bool> TrySendAsync(MimeMessage message, string kind, string toEmail, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(_options.Address, _options.AppPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send {Kind} email to {Email}.", kind, toEmail);
            return false;
        }
    }
}

/// <summary>What a session reminder email says - built by
/// SessionReminderNotificationService (and the Details page's test email).</summary>
public sealed record SessionReminderEmail(
    string Title,
    string? CourseTitle,
    string CourseColourHex,
    string TimeLabel,
    string? ForLabel,
    int MinutesUntilStart,
    string AppUrl);

public sealed class EmailOptions
{
    public string Address { get; set; } = string.Empty;

    public string AppPassword { get; set; } = string.Empty;
}
