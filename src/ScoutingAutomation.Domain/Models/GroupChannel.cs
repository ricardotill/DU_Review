using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Models;

/// <summary>A group and which of its channels a message came from.</summary>
public sealed record GroupChannel(Group Group, ChannelRole Role);

/// <summary>The Slack channels of a group, Toeristen first.</summary>
public static class GroupChannels
{
    /// <param name="adventurers">Whether the Avonturiers channel counts: false while the Avonturiers part is off.</param>
    public static IEnumerable<(string ChannelId, ChannelRole Role)> Of(Group group, bool adventurers)
    {
        if (!string.IsNullOrWhiteSpace(group.SlackChannelId)) yield return (group.SlackChannelId, ChannelRole.Toeristen);
        if (adventurers && !string.IsNullOrWhiteSpace(group.AdventurersSlackChannelId)) yield return (group.AdventurersSlackChannelId, ChannelRole.Avonturiers);
    }
}
