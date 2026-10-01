using FluentAssertions;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Storage;

public class SubmissionFileNameTests
{
    private readonly Group _group = new() { Name = "De Wolven", Abbreviation = "CDW" };
    private static readonly DateTimeOffset PostedAt = new(2026, 10, 1, 14, 32, 5, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(16, "IMG_1234.JPG", "CDW 16 14-32-05.jpg")]
    [InlineData(3, "opname.m4a", "CDW 3 14-32-05.m4a")]
    [InlineData(null, "foto.png", "CDW foto 14-32-05.png")]
    public void Build_ShouldUseAbbreviationAndAssignmentNumber(int? number, string original, string expected)
    {
        SubmissionFileName.Build(_group, number, original, PostedAt).Should().Be(expected);
    }

    [Fact]
    public void Build_WithoutAbbreviation_ShouldFallBackToGroupName()
    {
        var group = new Group { Name = "De Wolven" };

        SubmissionFileName.Build(group, 16, "foto.jpg", PostedAt).Should().Be("De Wolven 16 14-32-05.jpg");
    }

    [Fact]
    public void Build_ShouldRemoveInvalidFileNameCharacters()
    {
        var group = new Group { Name = "X", Abbreviation = "C/D\\W" };

        SubmissionFileName.Build(group, 1, "foto.jpg", PostedAt).Should().Be("CDW 1 14-32-05.jpg");
    }

    [Fact]
    public void Build_ShouldWriteTimeAsGiven()
    {
        var utc = new DateTimeOffset(2026, 10, 1, 12, 32, 5, TimeSpan.Zero);

        SubmissionFileName.Build(_group, 16, "foto.jpg", utc).Should().Be("CDW 16 12-32-05.jpg");
        SubmissionFileName.Build(_group, 16, "foto.jpg", utc.ToOffset(TimeSpan.FromHours(2))).Should().Be("CDW 16 14-32-05.jpg");
    }

    [Theory]
    [InlineData(1, "CDW 16 14-32-05 (1).jpg")]
    [InlineData(3, "CDW 16 14-32-05 (3).jpg")]
    [InlineData(null, "CDW 16 14-32-05.jpg")]
    public void Build_WithIndex_ShouldNumberBeforeTheExtension(int? index, string expected)
    {
        SubmissionFileName.Build(_group, 16, "IMG.JPG", PostedAt, index).Should().Be(expected);
    }
}
