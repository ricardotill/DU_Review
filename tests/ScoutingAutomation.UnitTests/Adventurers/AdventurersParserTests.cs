using FluentAssertions;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Adventurers;
using Xunit;

namespace ScoutingAutomation.UnitTests.Adventurers;

public class AdventurersParserTests
{
    [Theory]
    [InlineData("Locatie 7", "7")]
    [InlineData("locatie7 iedereen staat erop", "7")]
    [InlineData("Loc. 12", "12")]
    [InlineData("Location: 3", "3")]
    [InlineData("L7", "7")]
    [InlineData("l 07", "7")]
    [InlineData("#7", "7")]
    [InlineData("hier zijn we #  9", "9")]
    public void CheckIn_ShouldBeRead(string text, string number)
    {
        AdventurersParser.Parse(text).Should().Be(new AdventurersParseResult(SubmissionKind.CheckIn, number));
    }

    [Theory]
    [InlineData("Thema 3", "3")]
    [InlineData("theme 4", "4")]
    [InlineData("T3", "3")]
    [InlineData("Opdracht 3", "3")]
    [InlineData("Opdr 5", "5")]
    [InlineData("O3", "3")]
    [InlineData("o 03", "3")]
    public void Theme_ShouldBeRead(string text, string number)
    {
        AdventurersParser.Parse(text).Should().Be(new AdventurersParseResult(SubmissionKind.Theme, number));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Kijk ons eens!")]
    [InlineData("we waren om 12 uur bij de molen")]
    [InlineData("Locatie 0")]
    [InlineData("Locatie 123456")]
    public void NoNumber_ShouldFail(string? text)
    {
        AdventurersParser.Parse(text).Success.Should().BeFalse();
    }

    [Fact]
    public void SpelledOutWord_ShouldWinOverShortForm()
    {
        // "T2" comes first, but the spelled-out "Locatie 7" is what the team meant
        AdventurersParser.Parse("T2 groepje bij Locatie 7").Should().Be(new AdventurersParseResult(SubmissionKind.CheckIn, "7"));
    }

    [Fact]
    public void ShortForm_ShouldWinOverHash()
    {
        AdventurersParser.Parse("#4 en T3").Should().Be(new AdventurersParseResult(SubmissionKind.Theme, "3"));
    }

    [Fact]
    public void WithinATier_TheFirstShouldCount()
    {
        AdventurersParser.Parse("Thema 3 en Locatie 7").Should().Be(new AdventurersParseResult(SubmissionKind.Theme, "3"));
    }
}
