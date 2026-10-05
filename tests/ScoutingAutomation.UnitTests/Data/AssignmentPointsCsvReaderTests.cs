using FluentAssertions;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
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
        result[1].Code.Should().Be("2");
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
        result.Where(a => !AssignmentCode.IsLetter(a.Code)).Select(a => a.Code).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(n => n.ToString()));
    }

    [Fact]
    public void Read_WithTitleAndDescription_ShouldReadThem()
    {
        var csv = "Opdracht,standaard,awesome,titel,beschrijving\n" +
                  "1,1,2,Kampvuur,Maak een kampvuur zonder aansteker\n" +
                  "2,2,4,Toren,\"Bouw een toren, minstens 2 meter, met \"\"sjorringen\"\"\"\n" +
                  "3,1,2\n" +
                  "4,1,2,,\n";

        var result = AssignmentPointsCsvReader.Read(new StringReader(csv));

        result.Select(a => (a.Code, a.Title)).Should().Equal(("1", "Kampvuur"), ("2", "Toren"), ("3", null), ("4", null));
        result[0].Description.Should().Be("Maak een kampvuur zonder aansteker");
        result[1].Description.Should().Be("Bouw een toren, minstens 2 meter, met \"sjorringen\"");
        result[2].Description.Should().BeNull();
    }

    [Fact]
    public void Read_FromDutchExcel_ShouldKeepCommasAndLineBreaksInQuotedFields()
    {
        var csv = "Opdracht;standaard;awesome;titel;beschrijving\r\n" +
                  "1;1;2;Vuur;Hout, papier en geduld\r\n" +
                  "A;3;6;Speciaal;\"Eerste regel;\r\ntweede regel\"\r\n";

        var result = AssignmentPointsCsvReader.Read(new StringReader(csv));

        result.Select(a => a.Code).Should().Equal("1", "A");
        result[0].Description.Should().Be("Hout, papier en geduld");
        result[1].Description.Should().Be("Eerste regel;\ntweede regel");
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData(" B ", "B")]
    [InlineData("007", "7")]
    public void Read_Codes_ShouldBeStoredNormalised(string code, string expected)
    {
        var result = AssignmentPointsCsvReader.Read(new StringReader($"Opdracht,standaard,awesome\n{code},1,2"));

        result.Should().ContainSingle().Which.Code.Should().Be(expected);
    }

    [Theory]
    [InlineData("Opdracht,standaard,awesome\nA,1,2\na,2,4")]
    [InlineData("Opdracht,standaard,awesome\n7,1,2\n07,2,4")]
    [InlineData("Opdracht,standaard,awesome\nABCD,1,2")]
    [InlineData("Opdracht,standaard,awesome\nA1,1,2")]
    [InlineData("Opdracht,standaard,awesome\n0,1,2")]
    [InlineData("Opdracht,standaard,awesome,titel,beschrijving\n1,1,2,a,b,c")]
    [InlineData("Opdracht,standaard,awesome,titel\n1,1,2,\"open")]
    public void Read_InvalidCodesOrColumns_ShouldThrowFormatException(string csv)
    {
        var act = () => AssignmentPointsCsvReader.Read(new StringReader(csv));

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Read_TitleTooLong_ShouldThrowFormatException()
    {
        var act = () => AssignmentPointsCsvReader.Read(new StringReader($"Opdracht,standaard,awesome,titel\n1,1,2,{new string('x', 201)}"));

        act.Should().Throw<FormatException>().WithMessage("*title*");
    }
}
