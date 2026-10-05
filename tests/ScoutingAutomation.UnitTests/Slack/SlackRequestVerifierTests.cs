using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Slack.Events;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackRequestVerifierTests
{
    private const string Secret = "test-signing-secret";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"type\":\"event_callback\"}");

    private static SlackRequestVerifier CreateVerifier(bool simulationMode = false, string? secret = Secret) =>
        new(Options.Create(new ScoutingSettings { Slack = new SlackSettings { SimulationMode = simulationMode, SigningSecret = secret } }),
            new FixedTimeProvider(Now));

    private static string Timestamp(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString();

    [Fact]
    public void ComputeSignature_ShouldMatchSlackDocumentationExample()
    {
        // Example from https://api.slack.com/authentication/verifying-requests-from-slack
        var body = Encoding.UTF8.GetBytes("token=xyzz0WbapA4vBCDEFasx0q6G&team_id=T1DC2JH3J&team_domain=testteamnow&channel_id=G8PSS9T3V&channel_name=foobar&user_id=U2CERLKJA&user_name=roadrunner&command=%2Fwebhook-collect&text=&response_url=https%3A%2F%2Fhooks.slack.com%2Fcommands%2FT1DC2JH3J%2F397700885554%2F96rGlfmibIGlgcZRskXaIFfN&trigger_id=398738663015.47445629121.803a0bc887a14d10d2c447fce8b6703c");

        var signature = SlackRequestVerifier.ComputeSignature("8f742231b10e8888abcd99yyyzzz85a5", "1531420618", body);

        signature.Should().Be("v0=a2114d57b48eac39b9ad189dd8316235a7b4a8d21a10bd27519666489c69b503");
    }

    [Fact]
    public void Verify_ValidSignature_ShouldBeValid()
    {
        var ts = Timestamp(Now);
        var signature = SlackRequestVerifier.ComputeSignature(Secret, ts, Body);

        CreateVerifier().Verify(ts, signature, Body).Should().Be(SlackRequestVerification.Valid);
    }

    [Fact]
    public void Verify_TamperedBody_ShouldBeInvalid()
    {
        var ts = Timestamp(Now);
        var signature = SlackRequestVerifier.ComputeSignature(Secret, ts, Body);
        var tampered = Encoding.UTF8.GetBytes("{\"type\":\"event_callback\",\"x\":1}");

        CreateVerifier().Verify(ts, signature, tampered).Should().Be(SlackRequestVerification.InvalidSignature);
    }

    [Fact]
    public void Verify_OtherSecret_ShouldBeInvalid()
    {
        var ts = Timestamp(Now);
        var signature = SlackRequestVerifier.ComputeSignature("someone-else", ts, Body);

        CreateVerifier().Verify(ts, signature, Body).Should().Be(SlackRequestVerification.InvalidSignature);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public void Verify_TimestampOutsideFiveMinutes_ShouldBeStale(int minutes)
    {
        var ts = Timestamp(Now.AddMinutes(minutes));
        var signature = SlackRequestVerifier.ComputeSignature(Secret, ts, Body);

        CreateVerifier().Verify(ts, signature, Body).Should().Be(SlackRequestVerification.StaleTimestamp);
    }

    [Fact]
    public void Verify_NonNumericTimestamp_ShouldBeRejected()
    {
        CreateVerifier().Verify("yesterday", "v0=abc", Body).IsAllowed().Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "v0=abc")]
    [InlineData("1727697600", null)]
    [InlineData("", "")]
    public void Verify_MissingHeaders_ShouldBeRejected(string? ts, string? signature)
    {
        CreateVerifier().Verify(ts, signature, Body).Should().Be(SlackRequestVerification.MissingHeaders);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_LiveModeWithoutSecret_ShouldRejectEverything(string? secret)
    {
        var ts = Timestamp(Now);
        var signature = SlackRequestVerifier.ComputeSignature("", ts, Body);

        var result = CreateVerifier(secret: secret).Verify(ts, signature, Body);

        result.Should().Be(SlackRequestVerification.MissingSigningSecret);
        result.IsAllowed().Should().BeFalse();
    }

    [Fact]
    public void Verify_SimulationMode_ShouldSkipCheck()
    {
        var result = CreateVerifier(simulationMode: true, secret: null).Verify(null, null, Body);

        result.Should().Be(SlackRequestVerification.SkippedInSimulationMode);
        result.IsAllowed().Should().BeTrue();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
