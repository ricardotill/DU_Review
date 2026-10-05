using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Infrastructure.Slack.Events;

public enum SlackRequestVerification
{
    Valid,
    SkippedInSimulationMode,
    MissingSigningSecret,
    MissingHeaders,
    StaleTimestamp,
    InvalidSignature
}

public static class SlackRequestVerificationExtensions
{
    public static bool IsAllowed(this SlackRequestVerification result) =>
        result is SlackRequestVerification.Valid or SlackRequestVerification.SkippedInSimulationMode;
}

public interface ISlackRequestVerifier
{
    /// <summary>
    /// Checks the Slack request signature over the raw request body.
    /// See https://api.slack.com/authentication/verifying-requests-from-slack
    /// </summary>
    SlackRequestVerification Verify(string? timestamp, string? signature, byte[] body);
}

public class SlackRequestVerifier : ISlackRequestVerifier
{
    // Slack's recommended window; older requests may be replays
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(5);

    private const string Version = "v0";

    private readonly IOptions<ScoutingSettings> _options;
    private readonly TimeProvider _timeProvider;

    public SlackRequestVerifier(IOptions<ScoutingSettings> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public SlackRequestVerification Verify(string? timestamp, string? signature, byte[] body)
    {
        var slack = _options.Value.Slack;
        if (slack.SimulationMode)
        {
            return SlackRequestVerification.SkippedInSimulationMode;
        }

        // Fail closed: a live app without a secret cannot tell Slack from anyone else
        if (string.IsNullOrWhiteSpace(slack.SigningSecret))
        {
            return SlackRequestVerification.MissingSigningSecret;
        }

        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature))
        {
            return SlackRequestVerification.MissingHeaders;
        }

        if (!long.TryParse(timestamp, out var seconds))
        {
            return SlackRequestVerification.StaleTimestamp;
        }

        var age = _timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age.Duration() > MaxRequestAge)
        {
            return SlackRequestVerification.StaleTimestamp;
        }

        var expected = ComputeSignature(slack.SigningSecret, timestamp, body);
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));

        return matches ? SlackRequestVerification.Valid : SlackRequestVerification.InvalidSignature;
    }

    /// <summary>Returns the signature Slack would send: "v0=" + hex HMAC-SHA256 of "v0:{timestamp}:{body}".</summary>
    public static string ComputeSignature(string signingSecret, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{Version}:{timestamp}:");
        var baseString = new byte[prefix.Length + body.Length];
        prefix.CopyTo(baseString, 0);
        body.CopyTo(baseString, prefix.Length);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), baseString);
        return $"{Version}={Convert.ToHexStringLower(hash)}";
    }
}
