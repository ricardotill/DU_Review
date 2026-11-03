using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>A mail to one person: plain text, with a link to open.</summary>
public sealed record AccountMail(string To, string ToName, string Subject, string Body);

/// <summary>Sends the account mails: invitations, password resets and the test mail.</summary>
public interface IAccountMailer
{
    /// <summary>Whether mails really leave the app; otherwise they are only written to the log.</summary>
    bool CanSend { get; }

    /// <exception cref="AccountMailException">The SMTP server refused or could not be reached.</exception>
    Task SendAsync(AccountMail mail, CancellationToken ct = default);
}

public sealed class AccountMailException(string message, Exception inner) : Exception(message, inner);

/// <summary>Sends through the SMTP server in <see cref="ScoutingSettings.Email"/>; writes to the log without one.</summary>
public sealed class SmtpAccountMailer : IAccountMailer
{
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<SmtpAccountMailer> _logger;

    public SmtpAccountMailer(IOptions<ScoutingSettings> options, ILogger<SmtpAccountMailer> logger)
    {
        _options = options;
        _logger = logger;
    }

    private EmailSettings Settings => _options.Value.Email;

    public bool CanSend => Settings.IsConfigured;

    public async Task SendAsync(AccountMail mail, CancellationToken ct = default)
    {
        var settings = Settings;
        if (!settings.IsConfigured)
        {
            // Never the body: it holds a link that sets the password
            _logger.LogWarning("No SMTP server set (Scouting:Email): mail \"{Subject}\" to {To} not sent", mail.Subject, mail.To);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(new MailboxAddress(mail.ToName, mail.To));
        message.Subject = mail.Subject;
        message.Body = new TextPart("plain") { Text = mail.Body };

        try
        {
            using var client = new SmtpClient { Timeout = 30_000 };
            var security = settings.Security switch
            {
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls
            };
            await client.ConnectAsync(settings.Host, settings.Port, security, ct);
            if (!string.IsNullOrEmpty(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, settings.Password ?? "", ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
            _logger.LogInformation("Mail \"{Subject}\" sent to {To}", mail.Subject, mail.To);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Mail \"{Subject}\" to {To} could not be sent", mail.Subject, mail.To);
            throw new AccountMailException($"De mail kon niet worden verstuurd: {ex.Message}", ex);
        }
    }
}

/// <summary>The texts of the account mails, in Dutch.</summary>
public static class AccountMails
{
    public static AccountMail Invitation(AppUser user, string link) => new(user.Email!, user.DisplayName,
        "Je account voor DU Review",
        $"""
         Hoi {user.DisplayName},

         Er is een account voor je gemaakt in DU Review, voor {user.Email}.
         Kies via deze link je wachtwoord:

         {link}

         De link werkt {AccountService.LinkLifetimeText}. Daarna kan de organisatie je een nieuwe sturen.
         """);

    public static AccountMail PasswordReset(AppUser user, string link) => new(user.Email!, user.DisplayName,
        "Nieuw wachtwoord voor DU Review",
        $"""
         Hoi {user.DisplayName},

         Iemand heeft een nieuw wachtwoord gevraagd voor je account in DU Review ({user.Email}).
         Kies via deze link een nieuw wachtwoord:

         {link}

         De link werkt {AccountService.LinkLifetimeText}. Heb je dit niet zelf gevraagd? Dan hoef je niets te doen.
         """);

    public static AccountMail Test(string to) => new(to, to,
        "Testmail van DU Review",
        "Deze mail laat zien dat DU Review mails kan versturen. Je hoeft niets te doen.");
}
