using FluentAssertions;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests;

public class SubmissionFileNameTests
{
    private readonly Group _group = new() { Name = "De Wolven", Abbreviation = "CDW" };

    [Theory]
    [InlineData(16, "IMG_1234.JPG", "CDW 16.jpg")]
    [InlineData(3, "opname.m4a", "CDW 3.m4a")]
    [InlineData(null, "foto.png", "CDW foto.png")]
    public void Build_ShouldUseAbbreviationAndAssignmentNumber(int? number, string original, string expected)
    {
        SubmissionFileName.Build(_group, number, original).Should().Be(expected);
    }

    [Fact]
    public void Build_WithoutAbbreviation_ShouldFallBackToGroupName()
    {
        var group = new Group { Name = "De Wolven" };

        SubmissionFileName.Build(group, 16, "foto.jpg").Should().Be("De Wolven 16.jpg");
    }

    [Fact]
    public void Build_ShouldRemoveInvalidFileNameCharacters()
    {
        var group = new Group { Name = "X", Abbreviation = "C/D\\W" };

        SubmissionFileName.Build(group, 1, "foto.jpg").Should().Be("CDW 1.jpg");
    }
}
