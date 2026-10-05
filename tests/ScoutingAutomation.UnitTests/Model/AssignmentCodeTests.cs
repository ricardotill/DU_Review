using FluentAssertions;
using ScoutingAutomation.Domain.Models;
using Xunit;

namespace ScoutingAutomation.UnitTests.Model;

public class AssignmentCodeTests
{
    [Theory]
    [InlineData("12", "12")]
    [InlineData(" 012 ", "12")]
    [InlineData("a", "A")]
    [InlineData("Ab", "AB")]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("A1", null)]
    [InlineData("ABCD", null)]
    [InlineData("1.5", null)]
    [InlineData("é", null)]
    public void Normalize_ShouldGiveTheStoredCodeOrNull(string? raw, string? expected)
    {
        AssignmentCode.Normalize(raw).Should().Be(expected);
    }

    [Fact]
    public void Comparer_ShouldSortNumbersByValueThenLetters()
    {
        new[] { "B", "10", "A", "2", "AA", "1", "160" }.Order(AssignmentCode.Comparer)
            .Should().Equal("1", "2", "10", "160", "A", "AA", "B");
    }
}
