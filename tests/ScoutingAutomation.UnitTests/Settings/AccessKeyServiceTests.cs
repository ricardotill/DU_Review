using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Access;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

public class AccessKeyServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string KeysFile => Path.Combine(_temp.Path, "sub", "access-keys.json");

    private AccessKeyService Create(string? access = null, string? admin = null) =>
        new(Options.Create(new ScoutingSettings { KeysFile = KeysFile, AccessKey = access, AdminKey = admin }), NullLogger<AccessKeyService>.Instance);

    [Fact]
    public void Generate_ShouldGiveReadableRandomKeys()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => AccessKeyService.Generate()).ToList();

        keys.Should().OnlyContain(k => System.Text.RegularExpressions.Regex.IsMatch(k, "^[2-9A-HJ-NP-Z]{4}(-[2-9A-HJ-NP-Z]{4}){3}$"));
        keys.Distinct().Should().HaveCount(50);
    }

    [Fact]
    public void FirstStart_WithoutConfiguredKeys_ShouldGenerateAndSaveThem()
    {
        var service = Create();

        service.AccessKey.Should().MatchRegex("^[A-Z0-9-]{19}$");
        service.AccessStamp.Should().NotBeEmpty();
        service.AdminStamp.Should().NotBeEmpty();
        File.ReadAllText(KeysFile).Should().Contain(service.AccessKey);

        var restarted = Create();
        restarted.AccessKey.Should().Be(service.AccessKey);
        restarted.AccessStamp.Should().Be(service.AccessStamp);
    }

    [Fact]
    public void FirstStart_WithConfiguredKeys_ShouldUseThem()
    {
        var service = Create(access: " jury-key ", admin: "admin-key");

        service.AccessKey.Should().Be("jury-key");
        service.IsAccessKey("JURY-KEY").Should().BeTrue();
        service.IsAdminKey("admin-key").Should().BeTrue();
    }

    [Theory]
    [InlineData("jury-key", true)]
    [InlineData(" JURY-KEY ", true)]
    [InlineData("jury key", false)]
    [InlineData("juryKEY", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAccessKey_ShouldIgnoreCaseAndSpacesOnly(string? given, bool expected)
    {
        Create(access: "JURY-KEY").IsAccessKey(given).Should().Be(expected);
    }

    [Fact]
    public void Keys_ShouldNotBeInterchangeable()
    {
        var service = Create(access: "jury", admin: "admin");

        service.IsAccessKey("admin").Should().BeFalse();
        service.IsAdminKey("jury").Should().BeFalse();
    }

    [Fact]
    public void RegenerateAccessKey_ShouldReplaceKeyAndStampAndSurviveRestart()
    {
        var service = Create(access: "old-key");
        var oldStamp = service.AccessStamp;
        var adminStamp = service.AdminStamp;
        var changes = 0;
        service.Changed += () => changes++;

        var newKey = service.RegenerateAccessKey();

        service.AccessKey.Should().Be(newKey);
        service.AccessStamp.Should().NotBe(oldStamp);
        service.AdminStamp.Should().Be(adminStamp);
        service.IsAccessKey("old-key").Should().BeFalse();
        changes.Should().Be(1);
        Create(access: "old-key").AccessKey.Should().Be(newKey, "a saved key wins over the configured one");
    }

    [Fact]
    public void RegenerateAdminKey_ShouldReplaceOnlyTheAdminKey()
    {
        var service = Create(access: "jury", admin: "old-admin");
        var accessStamp = service.AccessStamp;
        var adminStamp = service.AdminStamp;
        var changes = 0;
        service.Changed += () => changes++;

        var newKey = service.RegenerateAdminKey();

        service.IsAdminKey(newKey).Should().BeTrue();
        service.IsAdminKey("old-admin").Should().BeFalse();
        service.AdminStamp.Should().NotBe(adminStamp);
        service.AccessStamp.Should().Be(accessStamp);
        changes.Should().Be(1);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    public void DamagedKeysFile_ShouldGetNewKeys(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeysFile)!);
        File.WriteAllText(KeysFile, content);

        var service = Create(access: "jury");

        service.IsAccessKey("jury").Should().BeTrue();
        File.ReadAllText(KeysFile).Should().Contain("jury");
    }

    [Fact]
    public void WithoutKeysFileSetting_ShouldUseTheDefaultLocation()
    {
        // A keys file left in the working folder would win over the configured keys, so it is moved aside
        var cwdFile = Path.GetFullPath("./access-keys.json");
        var aside = cwdFile + $".{Guid.NewGuid():N}.bak";
        var existed = File.Exists(cwdFile);
        if (existed) File.Move(cwdFile, aside);
        try
        {
            var service = new AccessKeyService(Options.Create(new ScoutingSettings { KeysFile = "", AccessKey = "a", AdminKey = "b" }), NullLogger<AccessKeyService>.Instance);

            service.IsAccessKey("a").Should().BeTrue();
            File.Exists(cwdFile).Should().BeTrue();
        }
        finally
        {
            if (existed) File.Move(aside, cwdFile, overwrite: true);
            else File.Delete(cwdFile);
        }
    }

    public void Dispose() => _temp.Dispose();
}

public class LoginAttemptLimiterTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Limiter_ShouldBlockAfterTooManyFailuresAndForgetThemAfterAMinute()
    {
        var clock = new Clock();
        var limiter = new LoginAttemptLimiter(clock);

        for (var i = 0; i < LoginAttemptLimiter.MaxFailures - 1; i++) limiter.RecordFailure("1.2.3.4");
        limiter.IsBlocked("1.2.3.4").Should().BeFalse();

        limiter.RecordFailure("1.2.3.4");
        limiter.IsBlocked("1.2.3.4").Should().BeTrue();
        limiter.IsBlocked("5.6.7.8").Should().BeFalse("addresses are counted separately");

        clock.Now += LoginAttemptLimiter.Window + TimeSpan.FromSeconds(1);
        limiter.IsBlocked("1.2.3.4").Should().BeFalse();
    }

    [Fact]
    public void Succeeded_ShouldTakeBackOnlyTheLastAttempt()
    {
        var limiter = new LoginAttemptLimiter(new Clock());
        for (var i = 0; i < LoginAttemptLimiter.MaxFailures - 1; i++) limiter.TryAttempt("x");

        // A right key: that attempt is not a failure
        limiter.TryAttempt("x").Should().BeTrue();
        limiter.Succeeded("x");
        limiter.IsBlocked("x").Should().BeFalse();

        // The wrong keys before it still count: a key of your own does not buy more guesses
        limiter.TryAttempt("x").Should().BeTrue();
        limiter.TryAttempt("x").Should().BeFalse();
    }

    [Fact]
    public void Succeeded_WithoutAttempts_ShouldDoNothing()
    {
        var limiter = new LoginAttemptLimiter(new Clock());

        limiter.Succeeded("x");

        limiter.IsBlocked("x").Should().BeFalse();
        limiter.TryAttempt("x").Should().BeTrue();
    }
}
