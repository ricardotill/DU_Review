using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Announcements;

/// <summary>
/// Posts the game-end message in every group channel: by itself when the event ends, or by hand from the app.
/// </summary>
public class GameEndAnnouncer : ScheduledAnnouncer
{
    public GameEndAnnouncer(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        RuntimeSettingsService runtime,
        TimeProvider time,
        ILogger<GameEndAnnouncer> logger)
        : base(scopeFactory, options, runtime, time, logger)
    {
    }

    protected override string Name => "game-end";

    protected override DateTimeOffset? PostTime => Runtime.EventEnd;

    protected override bool AutoPost => Options.Value.GameEndAnnouncement is { AutoPost: true };

    protected override string? Template => Runtime.Text(SlackText.GameEnd);

    protected override string Render(string template, string groupName) => Render(template, groupName, Runtime.EventEnd);

    /// <summary>The message for one group, with every placeholder filled in.</summary>
    /// <param name="end">The end of the event; <c>{end}</c> becomes its time of day in the game's time zone.</param>
    public static string Render(string template, string groupName, DateTimeOffset? end) =>
        template
            .Replace("{group}", groupName)
            .Replace("{end}", end?.ToGameTime().ToString("HH:mm") ?? "");

    public override DateTimeOffset? PostedAt(string channelId) => Runtime.GameEndPostedAt(channelId);

    protected override void MarkPosted(string channelId, DateTimeOffset postedAt) => Runtime.MarkGameEndPosted(channelId, postedAt);
}
