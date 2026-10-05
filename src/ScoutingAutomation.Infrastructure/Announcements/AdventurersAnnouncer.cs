using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Announcements;

/// <summary>
/// A message for every Avonturiers channel, posted at a set time or by hand. Does nothing while the Avonturiers part is off.
/// </summary>
public abstract class AdventurersAnnouncer : ScheduledAnnouncer
{
    protected AdventurersAnnouncer(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        RuntimeSettingsService runtime,
        TimeProvider time,
        ILogger logger)
        : base(scopeFactory, options, runtime, time, logger)
    {
    }

    /// <summary>The key the posted channels are saved under in the runtime settings.</summary>
    protected abstract string MessageKey { get; }

    protected override bool IsActive => Runtime.AdventurersEnabled;

    protected override string? ChannelOf(Group group) => group.AdventurersSlackChannelId;

    public override DateTimeOffset? PostedAt(string channelId) => Runtime.AdventurersPostedAt(MessageKey, channelId);

    protected override void MarkPosted(string channelId, DateTimeOffset postedAt) =>
        Runtime.MarkAdventurersPosted(MessageKey, channelId, postedAt);

    // Without the group, as in the preview: the map link is shown as a sample
    protected override string Render(string template, string groupName) =>
        Render(template, groupName, "https://…/kaart/…", Options.Value, Runtime.EventEnd);

    protected override string Render(string template, Group group) =>
        Render(template, group.Name, AdventurersLinks.MapUrl(Options.Value, group) ?? AdventurersLinks.NoMapLink, Options.Value, Runtime.EventEnd);

    /// <summary>The message for one group, with every placeholder filled in.</summary>
    /// <param name="end">The end of the event; <c>{time}</c> becomes its time of day in the game's time zone.</param>
    public static string Render(string template, string groupName, string mapLink, ScoutingSettings settings, DateTimeOffset? end)
    {
        var reactions = settings.ReviewStatusReactions ?? new ReviewStatusReactionsConfig();
        return template
            .Replace("{group}", groupName)
            .Replace("{map}", mapLink)
            .Replace("{time}", end?.ToGameTime().ToString("HH:mm") ?? "")
            .Replace("{approved}", Emoji(reactions.Approved))
            .Replace("{rejected}", Emoji(reactions.Rejected));
    }

    private static string Emoji(IEnumerable<string>? names) =>
        string.Join(" ", (names ?? Enumerable.Empty<string>())
            .Select(n => n.Trim().Trim(':'))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => $":{n}:"));
}

/// <summary>The start information (rules and the map link) for every Avonturiers channel.</summary>
public sealed class AdventurersStartAnnouncer : AdventurersAnnouncer
{
    public const string Key = "start";

    public AdventurersStartAnnouncer(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        RuntimeSettingsService runtime,
        TimeProvider time,
        ILogger<AdventurersStartAnnouncer> logger)
        : base(scopeFactory, options, runtime, time, logger)
    {
    }

    protected override string Name => "adventurers-start";

    protected override string MessageKey => Key;

    protected override DateTimeOffset? PostTime => Runtime.AdventurersStartAt;

    protected override bool AutoPost => Runtime.AdventurersOptions.StartAnnouncement is { AutoPost: true };

    protected override string? Template => Runtime.Text(SlackText.AdventurersStart);
}

/// <summary>The final location ('Destination Unknown') for every Avonturiers channel.</summary>
public sealed class AdventurersFinalAnnouncer : AdventurersAnnouncer
{
    public const string Key = "final";

    public AdventurersFinalAnnouncer(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        RuntimeSettingsService runtime,
        TimeProvider time,
        ILogger<AdventurersFinalAnnouncer> logger)
        : base(scopeFactory, options, runtime, time, logger)
    {
    }

    protected override string Name => "adventurers-final";

    protected override string MessageKey => Key;

    protected override DateTimeOffset? PostTime => Runtime.AdventurersFinalAt;

    protected override bool AutoPost => Runtime.AdventurersOptions.FinalAnnouncement is { AutoPost: true };

    protected override string? Template => Runtime.Text(SlackText.AdventurersFinal);
}
