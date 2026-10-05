using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>The key per group, saved with the other keys and found by the key itself at login.</summary>
public class GroupKeyServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string KeysFile => Path.Combine(_temp.Path, "access-keys.json");

    private AccessKeyService Create() =>
        new(Options.Create(new ScoutingSettings { KeysFile = KeysFile }), NullLogger<AccessKeyService>.Instance);

    [Fact]
    public void NewGroup_ShouldHaveNoKey()
    {
        var keys = Create();

        keys.GroupKey("C1").Should().BeNull();
        keys.GroupStamp("C1").Should().BeNull();
        keys.FindGroupChannel("anything").Should().BeNull();
    }

    [Fact]
    public void EnsureGroupKeys_ShouldOnlyMakeMissingKeysAndSurviveARestart()
    {
        var keys = Create();
        var first = keys.RegenerateGroupKey("C1");

        keys.EnsureGroupKeys(["C1", "C2", "C2", ""]).Should().Be(1);

        keys.GroupKey("C1").Should().Be(first);
        keys.GroupKey("C2").Should().NotBeNull().And.NotBe(first);
        keys.EnsureGroupKeys(["C1", "C2"]).Should().Be(0);

        var restarted = Create();
        restarted.GroupKey("C1").Should().Be(first);
        restarted.GroupStamp("C2").Should().Be(keys.GroupStamp("C2"));
    }

    [Fact]
    public void FindGroupChannel_ShouldIgnoreCaseAndSpacesButNotMatchOtherKeys()
    {
        var keys = Create();
        var key = keys.RegenerateGroupKey("C1");
        keys.RegenerateGroupKey("C2");

        keys.FindGroupChannel($" {key.ToLowerInvariant().Replace("-", " - ")} ").Should().Be("C1");
        keys.FindGroupChannel(keys.AccessKey).Should().BeNull();
        keys.FindGroupChannel("").Should().BeNull();
        keys.IsAccessKey(key).Should().BeFalse();
        keys.IsAdminKey(key).Should().BeFalse();
    }

    [Fact]
    public void RegenerateGroupKey_ShouldChangeOnlyThatGroupsStampAndTellListeners()
    {
        var keys = Create();
        keys.EnsureGroupKeys(["C1", "C2"]);
        var old = keys.GroupKey("C1");
        var stamp1 = keys.GroupStamp("C1");
        var stamp2 = keys.GroupStamp("C2");
        var changes = 0;
        keys.Changed += () => changes++;

        var key = keys.RegenerateGroupKey("C1");

        key.Should().NotBe(old);
        keys.FindGroupChannel(old).Should().BeNull();
        keys.GroupStamp("C1").Should().NotBe(stamp1);
        keys.GroupStamp("C2").Should().Be(stamp2);
        changes.Should().Be(1);
    }

    [Fact]
    public void RemoveGroupKey_ShouldStopTheKeyFromWorking()
    {
        var keys = Create();
        var key = keys.RegenerateGroupKey("C1");

        keys.RemoveGroupKey("C1").Should().BeTrue();

        keys.FindGroupChannel(key).Should().BeNull();
        keys.GroupStamp("C1").Should().BeNull();
        Create().GroupKey("C1").Should().BeNull();
        keys.RemoveGroupKey("C1").Should().BeFalse();
    }

    [Fact]
    public void OlderKeysFile_WithoutGroupKeys_ShouldKeepTheOtherKeys()
    {
        var original = Create();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(KeysFile))!.AsObject();
        json.Remove("GroupKeys");
        File.WriteAllText(KeysFile, json.ToJsonString());

        var restarted = Create();

        restarted.AccessKey.Should().Be(original.AccessKey);
        restarted.GroupKey("C1").Should().BeNull();
        restarted.RegenerateGroupKey("C1").Should().NotBeNull();
    }
}
