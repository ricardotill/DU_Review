using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Announcements;

/// <summary>
/// Posts the game-start message in every group channel: by itself when the event starts, or by hand from the app.
/// </summary>
public class GameStartAnnouncer : ScheduledAnnouncer
{
    public GameStartAnnouncer(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        RuntimeSettingsService runtime,
        TimeProvider time,
        ILogger<GameStartAnnouncer> logger)
        : base(scopeFactory, options, runtime, time, logger)
    {
    }

    protected override string Name => "game-start";

    protected override DateTimeOffset? PostTime => Runtime.EventStart;

    protected override bool AutoPost => Options.Value.GameStartAnnouncement is { AutoPost: true };

    protected override string? Template => Runtime.Text(SlackText.GameStart);

    protected override string Render(string template, string groupName) =>
        Render(template, groupName, Options.Value, Runtime.BlockAttemptsAfterApproval);

    public override DateTimeOffset? PostedAt(string channelId) => Runtime.GameStartPostedAt(channelId);

    protected override void MarkPosted(string channelId, DateTimeOffset postedAt) => Runtime.MarkGameStartPosted(channelId, postedAt);

    /// <summary>The message for one group, with every placeholder filled in.</summary>
    /// <param name="blockAttempts">Whether a new attempt after approval is blocked; it decides what <c>{retryRule}</c> says.</param>
    public static string Render(string template, string groupName, ScoutingSettings settings, bool blockAttempts = false)
    {
        var reactions = settings.ReviewStatusReactions ?? new ReviewStatusReactionsConfig();
        var feedback = settings.SlackFeedback ?? new SlackFeedbackConfig();
        // First, because the rule itself holds emoji placeholders
        return template
            .Replace("{retryRule}", blockAttempts ? GameStartAnnouncementConfig.RetryBlockedRule : GameStartAnnouncementConfig.RetryAllowedRule)
            .Replace("{group}", groupName)
            .Replace("{approved}", Emoji(reactions.Approved))
            .Replace("{awesome}", Emoji(reactions.Awesome))
            .Replace("{rejected}", Emoji(reactions.Rejected))
            .Replace("{missingNumber}", Emoji(feedback.MissingAssignmentNumber?.Reaction))
            .Replace("{unknownNumber}", Emoji(feedback.UnknownAssignmentNumber?.Reaction))
            .Replace("{unsupportedFile}", Emoji(feedback.UnsupportedFile?.Reaction))
            .Replace("{downloadFailed}", Emoji(feedback.DownloadFailed?.Reaction))
            .Replace("{alreadyCompleted}", Emoji(feedback.AlreadyCompleted?.Reaction));
    }

    private static string Emoji(string? name) => Emoji(name == null ? null : new[] { name });

    private static string Emoji(IEnumerable<string>? names) =>
        string.Join(" ", (names ?? Enumerable.Empty<string>())
            .Select(n => n.Trim().Trim(':'))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => $":{n}:"));
}
