using System.Globalization;

namespace ScoutingAutomation.Infrastructure.Slack;

public static class SlackTimestamp
{
    private static readonly decimal MaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>
    /// Converts a Slack message timestamp ("1727704800.123456", seconds since 1970) to a point in time.
    /// </summary>
    public static DateTimeOffset? ToDateTimeOffset(string? ts)
    {
        // Anything that is not a plausible time counts as no timestamp
        if (!decimal.TryParse(ts, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || seconds > MaxSeconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }

    /// <summary>
    /// Whether message <paramref name="ts"/> was posted before message <paramref name="other"/>. Slack orders messages by
    /// their exact timestamp (microseconds), which a converted time loses. False when either is not a timestamp.
    /// </summary>
    public static bool IsBefore(string? ts, string? other) =>
        decimal.TryParse(ts, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a)
        && decimal.TryParse(other, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var b)
        && a < b;

    /// <summary>The Slack "oldest"/"latest" parameter for a point in time.</summary>
    public static string FromDateTimeOffset(DateTimeOffset time) =>
        time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
