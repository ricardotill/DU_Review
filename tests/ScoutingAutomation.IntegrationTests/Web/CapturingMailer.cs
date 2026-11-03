using ScoutingAutomation.Infrastructure.Accounts;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>Stands in for the SMTP server: keeps every account mail, so a test can follow its link.</summary>
public sealed class CapturingMailer : IAccountMailer
{
    private readonly List<AccountMail> _sent = [];

    public bool CanSend => true;

    public IReadOnlyList<AccountMail> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task SendAsync(AccountMail mail, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(mail);
        return Task.CompletedTask;
    }

    /// <summary>The local part of the last link mailed to <paramref name="to"/>, e.g. "/wachtwoord-instellen?id=…".</summary>
    public string LastLinkTo(string to)
    {
        var body = Sent.Last(m => m.To == to).Body;
        var match = System.Text.RegularExpressions.Regex.Match(body, @"https?://[^/\s]+(?<path>/wachtwoord-instellen\?\S+)");
        return match.Success ? match.Groups["path"].Value : throw new InvalidOperationException("No password link in the mail");
    }
}
