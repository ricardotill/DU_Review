using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

public class RuntimeSettingsServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string File_ => Path.Combine(_temp.Path, "sub", "runtime-settings.json");

    private RuntimeSettingsService Create(bool manualMode = false, List<string>? reviewers = null) =>
        new(Options.Create(new ScoutingSettings
        {
            ManualMode = manualMode,
            Reviewers = reviewers ?? new List<string> { " U_CONFIG ", "" },
            RuntimeSettingsFile = File_
        }), NullLogger<RuntimeSettingsService>.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ManualMode_ShouldStartFromAppSettings(bool configured) =>
        Create(manualMode: configured).ManualMode.Should().Be(configured);

    [Fact]
    public void SetManualMode_ShouldNotifyOnlyOnChange()
    {
        var runtime = Create();
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.SetManualMode(false);
        runtime.SetManualMode(true);
        runtime.SetManualMode(true);

        runtime.ManualMode.Should().BeTrue();
        changes.Should().Be(1);
    }

    [Theory]
    [InlineData("U_CONFIG", true)]
    [InlineData("u_config", true)]
    [InlineData(" U_CONFIG ", true)]
    [InlineData("U_OTHER", false)]
    [InlineData(null, false)]
    [InlineData(" ", false)]
    public void IsReviewer_ShouldUseConfiguredReviewers(string? user, bool expected) =>
        Create().IsReviewer(user).Should().Be(expected);

    [Fact]
    public void ConfiguredReviewers_ShouldBeTrimmedAndSkipEmpty()
    {
        Create().ConfiguredReviewers.Should().Equal("U_CONFIG");
        new RuntimeSettingsService(Options.Create(new ScoutingSettings { Reviewers = null!, RuntimeSettingsFile = File_ }), NullLogger<RuntimeSettingsService>.Instance)
            .ConfiguredReviewers.Should().BeEmpty();
    }

    [Fact]
    public void AddReviewer_ShouldCountRightAwayAndSurviveRestart()
    {
        var runtime = Create();
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.AddReviewer(" U04ABC123 ").Should().BeNull();

        runtime.IsReviewer("u04abc123").Should().BeTrue();
        runtime.AddedReviewers.Should().Equal("U04ABC123");
        changes.Should().Be(1);
        Create().AddedReviewers.Should().Equal("U04ABC123");
        File.Exists(File_ + ".tmp").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("U")]
    [InlineData("U04 ABC")]
    [InlineData("<@U04ABC>")]
    public void AddReviewer_Invalid_ShouldExplain(string? id)
    {
        var runtime = Create();

        runtime.AddReviewer(id).Should().Contain("Slack-member-ID");
        runtime.AddedReviewers.Should().BeEmpty();
        File.Exists(File_).Should().BeFalse();
    }

    [Theory]
    [InlineData("U_CONFIG")]
    [InlineData("u_config")]
    public void AddReviewer_AlreadyThere_ShouldExplain(string id)
    {
        Create().AddReviewer(id).Should().Contain("is al jurylid");
    }

    [Fact]
    public void AddReviewer_Twice_ShouldExplain()
    {
        var runtime = Create();
        runtime.AddReviewer("U1234");

        runtime.AddReviewer("u1234").Should().Contain("is al jurylid");
        runtime.AddedReviewers.Should().ContainSingle();
    }

    [Fact]
    public void RemoveReviewer_ShouldOnlyRemoveReviewersAddedInTheApp()
    {
        var runtime = Create();
        runtime.AddReviewer("U1234");
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.RemoveReviewer("U_CONFIG").Should().BeFalse();
        runtime.RemoveReviewer("U9999").Should().BeFalse();
        runtime.RemoveReviewer("u1234").Should().BeTrue();

        runtime.IsReviewer("U1234").Should().BeFalse();
        runtime.IsReviewer("U_CONFIG").Should().BeTrue();
        changes.Should().Be(1);
        Create().AddedReviewers.Should().BeEmpty();
    }

    [Fact]
    public void SavedFile_ShouldSkipInvalidAndDuplicateEntries()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, """{"Reviewers":["U1","U1","u1","bad id","U2"]}""");

        Create().AddedReviewers.Should().Equal("U1", "U2");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void DamagedOrEmptyFile_ShouldStartWithoutAddedReviewers(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, content);

        var runtime = Create();

        runtime.AddedReviewers.Should().BeEmpty();
        runtime.IsReviewer("U_CONFIG").Should().BeTrue();
    }

    [Fact]
    public void WithoutFileSetting_ShouldUseDefaultLocation()
    {
        var runtime = new RuntimeSettingsService(Options.Create(new ScoutingSettings { RuntimeSettingsFile = "" }), NullLogger<RuntimeSettingsService>.Instance);

        runtime.AddedReviewers.Should().BeEmpty();
    }

    [Fact]
    public void TryClaimManualDownload_ShouldOnlySucceedOncePerFile()
    {
        var runtime = Create();

        runtime.TryClaimManualDownload("C1", "1.0", "a.jpg").Should().BeTrue();
        runtime.TryClaimManualDownload("C1", "1.0", "a.jpg").Should().BeFalse();
        runtime.TryClaimManualDownload("C1", "1.0", "b.jpg").Should().BeTrue();
        runtime.TryClaimManualDownload("C1", "2.0", "a.jpg").Should().BeTrue();
    }

    public void Dispose() => _temp.Dispose();
}
