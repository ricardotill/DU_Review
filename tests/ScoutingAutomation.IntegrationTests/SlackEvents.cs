using System.Globalization;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// Slack events and history messages as Slack sends them, for posting to the app or serving from <see cref="FakeSlackApi"/>.
/// </summary>
public static class SlackEvents
{
    public const string Scout = "U_SCOUT";

    private static int _sequence;

    /// <summary>A unique Slack timestamp, <paramref name="secondsFromNow"/> away from now.</summary>
    public static string Ts(double secondsFromNow = 0) => Ts(DateTimeOffset.UtcNow.AddSeconds(secondsFromNow));

    /// <summary>A unique Slack timestamp at <paramref name="at"/>.</summary>
    public static string Ts(DateTimeOffset at) =>
        $"{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}.{Interlocked.Increment(ref _sequence) % 1_000_000:D6}";

    /// <summary>A file hosted by <see cref="FakeSlackApi"/>.</summary>
    public static object File(string name, string mimetype = "image/jpeg", string? id = null) =>
        new { id = id ?? $"F_{name}", name, mimetype, size = 3, url_private_download = $"https://files.slack.com/{name}" };

    /// <summary>A file deleted from its message, as Slack still lists it.</summary>
    public static object DeletedFile(string id) => new { id, mode = "tombstone" };

    /// <summary>A message with files posted by a scout.</summary>
    public static object Message(string channel, string ts, string text, params object[] files) =>
        Message(channel, ts, text, Scout, "file_share", files);

    public static object Message(string channel, string ts, string text, string user, string? subtype, params object[] files) =>
        new { type = "message", subtype, channel, user, text, ts, files };

    public static object Reaction(string channel, string ts, string reaction, string user = TestApp.Reviewer) =>
        new { type = "reaction_added", user, reaction, item = new { type = "message", channel, ts } };

    public static object ReactionRemoved(string channel, string ts, string reaction, string user = TestApp.Reviewer) =>
        new { type = "reaction_removed", user, reaction, item = new { type = "message", channel, ts } };

    /// <summary>An edit of a scout's message: Slack sends the message as it is now, and as it was.</summary>
    public static object Edited(string channel, string ts, string oldText, string newText, object[] files, string? botId = null) =>
        new
        {
            type = "message",
            subtype = "message_changed",
            channel,
            ts = Ts(),
            message = new { type = "message", user = Scout, bot_id = botId, text = newText, ts, files },
            previous_message = new { type = "message", user = Scout, text = oldText, ts }
        };

    public static object Deleted(string channel, string ts, string text) =>
        new
        {
            type = "message",
            subtype = "message_deleted",
            channel,
            ts = Ts(),
            deleted_ts = ts,
            previous_message = new { type = "message", user = Scout, text, ts }
        };

    /// <summary>A message as conversations.history returns it.</summary>
    public static object HistoryMessage(string ts, string text, object[] files, object[]? reactions = null, string user = Scout) =>
        new { type = "message", ts, user, text, files, reactions = reactions ?? Array.Empty<object>() };

    /// <summary>A reaction as Slack lists it on a message.</summary>
    public static object Reacted(string name, params string[] users) => new { name, users, count = users.Length };
}
