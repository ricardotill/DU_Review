using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Access;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>Saved settings that are damaged, cannot be written, changed elsewhere, or hold values the app cannot use.</summary>
public class SettingsFilesHardeningTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 3, 12, 0, 0);

    private readonly TempDirectory _temp = new();

    private string SettingsFile => Path.Combine(_temp.Path, "runtime-settings.json");

    private RuntimeSettingsService Runtime() =>
        TestRuntime.For(Options.Create(new ScoutingSettings { EventStart = Start, RuntimeSettingsFile = SettingsFile }));

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
        var runtime = TestRuntime.For(Options.Create(new ScoutingSettings { EventStart = DateTime.MinValue, RuntimeSettingsFile = SettingsFile }));

        runtime.EventStart.Should().BeNull();
    }

    [Fact]
    public void DamagedSavedSettings_ShouldNotStopTheApp()
    {
        var runtime = TestRuntime.Create(new ScoutingSettings { EventStart = Start, BlockAttemptsAfterApproval = true }, new InMemoryRuntimeSettingsStore("{ broken"));

        runtime.EventStartGameTime.Should().Be(Start);
        runtime.BlockAttemptsAfterApproval.Should().BeTrue("the settings from appsettings apply");
        runtime.SetBlockAttemptsAfterApproval(false);
        runtime.BlockAttemptsAfterApproval.Should().BeFalse("a save replaces the damaged settings");
    }

    [Fact]
    public void Settings_ThatCannotBeSaved_ShouldNotChangeInTheAppEither()
    {
        var store = new FailingStore();
        var runtime = TestRuntime.Create(new ScoutingSettings { EventStart = Start }, store);
        store.Fail = true;

        var act = () => runtime.SetEventStart(Start.AddHours(1));

        // An IOException, so the pages show "Opslaan is mislukt" as before
        act.Should().Throw<RuntimeSettingsSaveException>().Which.Should().BeAssignableTo<IOException>();
        runtime.EventStartGameTime.Should().Be(Start, "a restart would lose a change that was never saved");
    }

    [Fact]
    public void ChangeThatCannotBeSavedOrReadBack_ShouldNotStayInEffect()
    {
        var store = new FailingStore();
        var runtime = TestRuntime.Create(new ScoutingSettings { ManualMode = false }, store);
        runtime.SetManualMode(true);
        store.Fail = true;
        store.FailLoad = true;

        var act = () => runtime.SetManualMode(false);

        act.Should().Throw<RuntimeSettingsSaveException>();
        runtime.ManualMode.Should().BeTrue("the last saved settings apply, not the change that failed");
    }

    [Fact]
    public void DatabaseThatCannotBeRead_ShouldKeepTheSettingsInMemory()
    {
        var store = new FailingStore();
        var runtime = TestRuntime.Create(new ScoutingSettings { ManualMode = false }, store);
        runtime.SetManualMode(true);
        store.FailLoad = true;

        runtime.Reload();

        runtime.ManualMode.Should().BeTrue("a short database outage must not switch manual mode off");
    }

    [Fact]
    public void SaveBasedOnOldSettings_ShouldBeRefusedAndShowTheNewOnes()
    {
        // Two instances of the app on one database
        var store = new InMemoryRuntimeSettingsStore();
        var first = TestRuntime.Create(new ScoutingSettings(), store);
        var second = TestRuntime.Create(new ScoutingSettings(), store);
        first.AddReviewer("U_EERSTE").Should().BeNull();

        var act = () => second.AddReviewer("U_TWEEDE");

        act.Should().Throw<RuntimeSettingsSaveException>().WithMessage("*ergens anders gewijzigd*");
        second.AddedReviewers.Should().Equal(["U_EERSTE"], "the settings are read again, so the admin sees what changed");
        second.AddReviewer("U_TWEEDE").Should().BeNull("trying again works");
        first.Reload();
        first.AddedReviewers.Should().Equal("U_EERSTE", "U_TWEEDE");
    }

    [Fact]
    public void RecordOfAPostedMessage_ShouldNotBeLostWhenAnotherInstanceSavedFirst()
    {
        var store = new InMemoryRuntimeSettingsStore();
        var first = TestRuntime.Create(new ScoutingSettings(), store);
        var second = TestRuntime.Create(new ScoutingSettings(), store);
        first.AddReviewer("U_EERSTE").Should().BeNull();
        var group = Guid.NewGuid();

        // The notice is already in Slack: its record must be kept, on top of the other instance's change
        second.MarkUnlockNotified(group, 3);

        var restarted = TestRuntime.Create(new ScoutingSettings(), store);
        restarted.UnlockNotified(group).Should().Be(3);
        restarted.AddedReviewers.Should().Equal("U_EERSTE");
    }

    [Fact]
    public void ManualMode_ShouldBeSharedAndKept()
    {
        var store = new InMemoryRuntimeSettingsStore();
        var first = TestRuntime.Create(new ScoutingSettings { ManualMode = false }, store);
        var changed = 0;
        var second = TestRuntime.Create(new ScoutingSettings { ManualMode = false }, store);
        second.Changed += () => changed++;

        first.SetManualMode(true);
        second.Reload();

        second.ManualMode.Should().BeTrue();
        changed.Should().Be(1);
        TestRuntime.Create(new ScoutingSettings { ManualMode = false }, store).ManualMode.Should().BeTrue("a restart keeps it");
    }

    private sealed class FailingStore : IRuntimeSettingsStore
    {
        private readonly InMemoryRuntimeSettingsStore _inner = new();

        public bool Fail { get; set; }

        public bool FailLoad { get; set; }

        public (string? Json, long Version) Load() => FailLoad ? throw new InvalidOperationException("database down") : _inner.Load();

        public long? TrySave(string json, long expectedVersion) =>
            Fail ? throw new InvalidOperationException("database down") : _inner.TrySave(json, expectedVersion);
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
