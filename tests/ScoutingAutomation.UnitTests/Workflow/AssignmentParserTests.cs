using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

public class AssignmentParserTests
{
    private readonly AssignmentParser _parser = new(NullLogger<AssignmentParser>.Instance);

    [Theory]
    [InlineData("Opdracht 1: Ons kampvuur!", 1)]
    [InlineData("opdracht 5 klaar", 5)]
    [InlineData("Assignment 12 submitted", 12)]
    [InlineData("taak 3 afgerond", 3)]
    [InlineData("challenge - 7", 7)]
    [InlineData("#2 Hier is de foto", 2)]
    [InlineData("Hier is foto voor # 15", 15)]
    [InlineData("A4 tocht voltooid", 4)]
    [InlineData("a10 resultaat", 10)]
    [InlineData("6 - pionieren toren", 6)]
    [InlineData("8: vogelhuisje", 8)]
    public void Parse_ValidMessagePatterns_ShouldExtractCorrectAssignmentNumber(string text, int expectedNumber)
    {
        // Act
        var result = _parser.Parse(text);

        // Assert
        result.Success.Should().BeTrue();
        result.AssignmentCode.Should().Be(expectedNumber.ToString());
        result.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("Opdracht A", "A")]
    [InlineData("opdracht b: onze toren", "B")]
    [InlineData("Taak: c klaar", "C")]
    [InlineData("#A de speciale opdracht", "A")]
    [InlineData("Hier is # d", "D")]
    [InlineData("Opdracht AB", "AB")]
    public void Parse_SpecialAssignmentLetters_ShouldGiveTheCodeInUpperCase(string text, string expected)
    {
        var result = _parser.Parse(text);

        result.Success.Should().BeTrue();
        result.AssignmentCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("A12 kampvuur", "12")]
    [InlineData("taak a 12", "12")]
    [InlineData("Opdracht A, foto 3 - toren", "A")]
    [InlineData("opdracht a #5", "5")]
    public void Parse_NumbersBeforeLetters_ShouldPreferTheNumber(string text, string expected)
    {
        _parser.Parse(text).AssignmentCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("a photo of our camp")]
    [InlineData("Hier is A en B")]
    [InlineData("Opdrachtenlijst bekeken")]
    [InlineData("opdracht abcd")]
    public void Parse_LettersWithoutKeywordOrHash_ShouldNotBeACode(string text)
    {
        _parser.Parse(text).Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Hallo leiding, we zijn er bijna!")]
    [InlineData("Gezellige avond iedereen")]
    public void Parse_InvalidOrMissingPatterns_ShouldFailGracefully(string? text)
    {
        // Act
        var result = _parser.Parse(text);

        // Assert
        result.Success.Should().BeFalse();
        result.AssignmentCode.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
