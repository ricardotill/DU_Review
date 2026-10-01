using FluentAssertions;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Data;

public class AssignmentPointsCsvReaderTests
{
    [Fact]
    public void Read_ValidCsv_ShouldReturnAssignmentsWithPoints()
    {
        // Arrange
        var csv = "Opdracht,standaard,awesome\r\n1,1,2\r\n2,2,4\r\n\r\n3,1,2";

        // Act
        var result = AssignmentPointsCsvReader.Read(new StringReader(csv));

        // Assert
        result.Should().HaveCount(3);
        result[1].Number.Should().Be(2);
        result[1].StandardPoints.Should().Be(2);
        result[1].AwesomePoints.Should().Be(4);
    }

    [Theory]
    [InlineData("Opdracht,standaard,awesome\n1,1")]
    [InlineData("Opdracht,standaard,awesome\n1,een,2")]
    [InlineData("Opdracht,standaard,awesome\n1,1,2\n1,2,4")]
    public void Read_InvalidCsv_ShouldThrowFormatException(string csv)
    {
        // Act
        var act = () => AssignmentPointsCsvReader.Read(new StringReader(csv));

        // Assert
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Read_ProjectCsvFile_ShouldParseAllAssignments()
    {
        // Arrange
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../../src/ScoutingAutomation.Web/opdrachtenpunten.csv");
        using var reader = new StreamReader(path);

        // Act
        var result = AssignmentPointsCsvReader.Read(reader);

        // Assert
        result.Should().HaveCount(160);
        result.Select(a => a.Number).Should().BeEquivalentTo(Enumerable.Range(1, 160));
    }
}
