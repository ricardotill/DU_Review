using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScoutingAutomation.Infrastructure.Services;
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
        result.AssignmentNumber.Should().Be(expectedNumber);
        result.ErrorMessage.Should().BeNull();
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
        result.AssignmentNumber.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
