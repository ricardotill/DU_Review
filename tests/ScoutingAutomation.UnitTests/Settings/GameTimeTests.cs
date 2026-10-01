using FluentAssertions;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

public class GameTimeTests
{
    [Fact]
    public void ToGameTime_ShouldUseAmsterdamTimeWhateverTheServerZone()
    {
        var summer = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).ToGameTime();
        var winter = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero).ToGameTime();

        summer.Hour.Should().Be(14);
        summer.Offset.Should().Be(TimeSpan.FromHours(2));
        winter.Hour.Should().Be(13);
    }

    [Fact]
    public void FromGameTime_ShouldReadAChosenTimeAsAmsterdamTime()
    {
        GameTime.FromGameTime(new DateTime(2026, 10, 1, 9, 0, 0)).UtcDateTime.Should().Be(new DateTime(2026, 10, 1, 7, 0, 0));
    }
}
