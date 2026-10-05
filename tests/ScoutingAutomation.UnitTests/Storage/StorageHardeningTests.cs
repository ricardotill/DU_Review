using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Storage;
using ScoutingAutomation.Web.Security;
using Xunit;

namespace ScoutingAutomation.UnitTests.Storage;

/// <summary>File and folder names from Slack and the settings, leftovers of a crash, and the points file.</summary>
public class StorageHardeningTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly Group Wolves = new() { Name = "De Wolven", Abbreviation = "DWO" };
    private static readonly DateTimeOffset At = new(2026, 10, 3, 14, 32, 5, TimeSpan.Zero);

    private LocalFileStorageService Storage() =>
        new(Options.Create(new ScoutingSettings { StorageRoot = _temp.Path }), NullLogger<LocalFileStorageService>.Instance);

    [Theory]
    [InlineData("foto.JPG", "DWO 4 14-32-05.jpg")]
    [InlineData("clip.mp4", "DWO 4 14-32-05.mp4")]
    [InlineData("rare.j:p*g", "DWO 4 14-32-05")]
    [InlineData("geen-extensie", "DWO 4 14-32-05")]
    public void FileName_ShouldOnlyKeepAPlainExtension(string original, string expected)
    {
        SubmissionFileName.Build(Wolves, "4", original, At).Should().Be(expected);
    }

    [Fact]
    public async Task FileName_FromAVeryLongOriginalName_ShouldStillBeStorable()
    {
        var original = new string('a', 300) + "." + new string('b', 200);

        var name = SubmissionFileName.Build(Wolves, assignmentCode: null, original, At);
        var path = await Storage().SaveInitialFileAsync("De Wolven", name, new MemoryStream(new byte[] { 1 }));

        File.Exists(path).Should().BeTrue();
        Path.GetFileName(path).Length.Should().BeLessThan(255);
    }

    [Theory]
    [InlineData("De Wolven", "de-wolven")]
    [InlineData("_backup", "group_backup")]
    [InlineData("_deleted", "group_deleted")]
    [InlineData("CON", "con_")]
    [InlineData("Wolven.", "wolven")]
    [InlineData("a:b*c", "a_b_c")]
    [InlineData("  ", "group")]
    [InlineData("..", "group")]
    public void FolderName_ShouldNeverClashWithTheAppsOwnFoldersOrBreakOnWindows(string group, string expected)
    {
        LocalFileStorageService.FolderName(group).Should().Be(expected);
    }

    [Fact]
    public void Start_ShouldRemoveUnfinishedDownloadsLeftByACrash()
    {
        var group = Path.Combine(_temp.Path, "de-wolven");
        Directory.CreateDirectory(group);
        var part = Path.Combine(group, $".{Guid.NewGuid():N}.part");
        var real = Path.Combine(group, "DWO 4.jpg");
        File.WriteAllText(part, "half");
        File.WriteAllText(real, "photo");

        Storage();

        File.Exists(part).Should().BeFalse();
        File.Exists(real).Should().BeTrue();
    }

    [Fact]
    public async Task MoveToPath_ShouldPutAFileBackWithoutOverwritingAnother()
    {
        var storage = Storage();
        var original = await storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new MemoryStream(new byte[] { 1 }));
        var moved = await storage.MoveFileToStatusAsync("De Wolven", original, Domain.Enums.SubmissionStatus.Approved);
        await File.WriteAllBytesAsync(original, new byte[] { 9 });

        var back = await storage.MoveToPathAsync(moved, original);

        back.Should().NotBe(original);
        (await File.ReadAllBytesAsync(original)).Should().Equal(9);
        (await File.ReadAllBytesAsync(back)).Should().Equal(1);
    }

    [Theory]
    [InlineData("Opdracht;standaard;awesome\n1;1;2\n2;2;4")]
    [InlineData("Opdracht,standaard,awesome\n1,1,2,\n2,2,4,")]
    [InlineData("Opdracht,standaard,awesome,\r\n1,1,2,,\r\n2,2,4")]
    public void PointsFile_FromExcel_ShouldBeRead(string csv)
    {
        AssignmentPointsCsvReader.Read(new StringReader(csv)).Select(a => (a.Code, a.StandardPoints, a.AwesomePoints))
            .Should().Equal(("1", 1, 2), ("2", 2, 4));
    }

    [Fact]
    public void PointsFile_WithoutHeader_ShouldNotLoseTheFirstAssignment()
    {
        AssignmentPointsCsvReader.Read(new StringReader("1,1,2\n2,2,4")).Should().HaveCount(2);
    }

    [Fact]
    public void PointsFile_WithNegativePoints_ShouldBeRefused()
    {
        var act = () => AssignmentPointsCsvReader.Read(new StringReader("Opdracht,standaard,awesome\n1,-1,2"));

        act.Should().Throw<FormatException>().WithMessage("*line 2*");
    }

    [Theory]
    [InlineData("foto.jpg", "image/jpeg")]
    [InlineData("clip.MOV", "video/quicktime")]
    [InlineData("doc.pdf", "application/pdf")]
    [InlineData("page.html", SafeContentType.Fallback)]
    [InlineData("logo.svg", SafeContentType.Fallback)]
    [InlineData("script.js", SafeContentType.Fallback)]
    [InlineData("geen", SafeContentType.Fallback)]
    public void ServedFile_ShouldNeverGetATypeABrowserRuns(string fileName, string expected)
    {
        SafeContentType.For(fileName).Should().Be(expected);
    }

    public void Dispose() => _temp.Dispose();
}
