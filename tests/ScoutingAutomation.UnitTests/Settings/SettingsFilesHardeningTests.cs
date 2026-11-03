using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Access;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>Settings files that are damaged, cannot be written, or hold values the app cannot use.</summary>
public class SettingsFilesHardeningTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 3, 12, 0, 0);

    private readonly TempDirectory _temp = new();

    private string SettingsFile => Path.Combine(_temp.Path, "runtime-settings.json");

    private RuntimeSettingsService Runtime() =>
        new(Options.Create(new ScoutingSettings { EventStart = Start, RuntimeSettingsFile = SettingsFile }), NullLogger<RuntimeSettingsService>.Instance);

    [Theory]
    [InlineData(1)]
    [InlineData(1999)]
    [InlineData(2100)]
    public void EventTime_OutsideTheAllowedYears_ShouldBeRefused(int year)
    {
        var runtime = Runtime();

        var error = runtime.SetEventStart(new DateTime(year, 1, 1, 12, 0, 0));

        error.Should().Contain("2000");
        runtime.EventStartGameTime.Should().Be(Start);
        runtime.EventStart.Should().NotBeNull("converting the time must still work");
    }

    [Fact]
    public void EventTime_OutsideTheAllowedYearsInTheFile_ShouldBeIgnoredSoTheAppStillStarts()
    {
        // Written by an older version, or by hand: year 1 cannot be converted between time zones
        File.WriteAllText(SettingsFile, """{"EventStart":{"Value":"0001-01-01T00:00:00"}}""");

        var runtime = Runtime();

        runtime.EventStartGameTime.Should().Be(Start);
        runtime.IsBeforeEventStart("1727704800.000000").Should().BeTrue();
    }

    [Fact]
    public void EventTime_OutsideTheAllowedYearsInAppSettings_ShouldBeIgnored()
    {
        var runtime = new RuntimeSettingsService(
            Options.Create(new ScoutingSettings { EventStart = DateTime.MinValue, RuntimeSettingsFile = SettingsFile }),
            NullLogger<RuntimeSettingsService>.Instance);

        runtime.EventStart.Should().BeNull();
    }

    [Fact]
    public void DamagedSettingsFile_ShouldBeSetAsideInsteadOfOverwritten()
    {
        File.WriteAllText(SettingsFile, "{ broken");

        var runtime = Runtime();
        runtime.SetBlockAttemptsAfterApproval(false);

        var aside = Directory.GetFiles(_temp.Path, "runtime-settings.json.corrupt-*").Should().ContainSingle().Subject;
        File.ReadAllText(aside).Should().Be("{ broken");
        File.ReadAllText(SettingsFile).Should().Contain("BlockAttemptsAfterApproval");
    }

    [Fact]
    public void Settings_ThatCannotBeSaved_ShouldNotChangeInTheAppEither()
    {
        var runtime = Runtime();
        // A folder where the file should be: every save fails, like a full disk or a read-only folder
        Directory.CreateDirectory(SettingsFile);

        var act = () => runtime.SetEventStart(Start.AddHours(1));

        act.Should().Throw<Exception>().Which.Should().Match(e => e is IOException || e is UnauthorizedAccessException);
        runtime.EventStartGameTime.Should().Be(Start, "a restart would lose a change that was never saved");
        Directory.GetFiles(_temp.Path, "*.tmp").Should().BeEmpty();
    }

    public void Dispose() => _temp.Dispose();
}

public class LoginAttemptLimiterHardeningTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Attempts_SentAtTheSameMoment_ShouldNotAllGetThrough()
    {
        var limiter = new LoginAttemptLimiter(new Clock());

        var allowed = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => limiter.TryAttempt("1.2.3.4"))));

        allowed.Count(a => a).Should().Be(LoginAttemptLimiter.MaxFailures);
    }

    [Fact]
    public void RightKey_ShouldNotCountAsAFailure()
    {
        var limiter = new LoginAttemptLimiter(new Clock());
        for (var i = 0; i < LoginAttemptLimiter.MaxFailures - 1; i++) limiter.TryAttempt("x");

        limiter.TryAttempt("x").Should().BeTrue();
        limiter.Succeeded("x");

        limiter.IsBlocked("x").Should().BeFalse();
    }

    [Fact]
    public void ManyAddresses_ShouldBeForgottenOnceTheirFailuresAreOld()
    {
        var clock = new Clock();
        var limiter = new LoginAttemptLimiter(clock);
        for (var i = 0; i < LoginAttemptLimiter.PruneAbove; i++) limiter.RecordFailure($"10.0.{i / 256}.{i % 256}");

        clock.Now += LoginAttemptLimiter.Window + TimeSpan.FromSeconds(1);
        limiter.RecordFailure("new");

        limiter.TrackedClients.Should().Be(1);
    }
}
