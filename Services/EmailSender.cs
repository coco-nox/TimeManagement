using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace TimeManagement.Services;

/// <summary>
/// Sends transactional email (currently just password reset links) via
/// Gmail's SMTP server using MailKit. Address/AppPassword come from
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

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(_options.Address, _options.AppPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            // Same reasoning as the missing-config case above: logged, not
            // thrown, so a Gmail-side hiccup can't reveal anything to the
            // caller or break the "always show the same message" flow.
            _logger.LogError(ex, "Failed to send password reset email to {Email}.", toEmail);
        }
    }
}

public sealed class EmailOptions
{
    public string Address { get; set; } = string.Empty;

    public string AppPassword { get; set; } = string.Empty;
}
