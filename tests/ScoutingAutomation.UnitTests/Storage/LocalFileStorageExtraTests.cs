using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Storage;

public class LocalFileStorageExtraTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly LocalFileStorageService _storage;

    public LocalFileStorageExtraTests()
    {
        _storage = new LocalFileStorageService(
            Options.Create(new ScoutingSettings { StorageRoot = _temp.Path }),
            NullLogger<LocalFileStorageService>.Instance);
    }

    [Fact]
    public async Task OpenReadStreamAsync_ShouldReturnContentOrNull()
    {
        var path = await _storage.SaveInitialFileAsync("De Wolven", "a.txt", new MemoryStream(Encoding.UTF8.GetBytes("hallo")));

        (await _storage.FileExistsAsync(path)).Should().BeTrue();
        await using (var stream = await _storage.OpenReadStreamAsync(path))
        {
            new StreamReader(stream!).ReadToEnd().Should().Be("hallo");
        }

        (await _storage.FileExistsAsync(path + ".missing")).Should().BeFalse();
        (await _storage.OpenReadStreamAsync(path + ".missing")).Should().BeNull();
    }

    [Fact]
    public async Task SaveInitialFileAsync_WithoutName_ShouldGenerateOne()
    {
        var path = await _storage.SaveInitialFileAsync("De Wolven", "", new MemoryStream(new byte[] { 1 }));

        Path.GetFileName(path).Should().StartWith("file_");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task MoveFileToStatusAsync_MissingSource_ShouldKeepThePathAndWriteNothing()
    {
        var missing = Path.Combine(_temp.Path, "de-wolven", "weg.jpg");

        var path = await _storage.MoveFileToStatusAsync("De Wolven", missing, SubmissionStatus.Approved);

        path.Should().Be(missing);
        File.Exists(Path.Combine(_temp.Path, "de-wolven", "approved", "weg.jpg")).Should().BeFalse("a stand-in would hide that the file is gone");
    }

    [Fact]
    public async Task MoveFileToStatusAsync_MissingSource_ShouldNeverOverwriteAFileWithTheSameName()
    {
        var real = Path.Combine(_temp.Path, "de-wolven", "approved", "DWO 4.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        await File.WriteAllBytesAsync(real, new byte[] { 1, 2, 3 });

        await _storage.MoveFileToStatusAsync("De Wolven", Path.Combine(_temp.Path, "de-wolven", "DWO 4.jpg"), SubmissionStatus.Approved);

        (await File.ReadAllBytesAsync(real)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task MoveFileToStatusAsync_FileMovedEarlier_ShouldBeFoundInAnotherStatusFolder()
    {
        // A move whose database save failed: the file is in "rejected", the database still says the group folder
        var stale = Path.Combine(_temp.Path, "de-wolven", "DWO 4.jpg");
        var actual = Path.Combine(_temp.Path, "de-wolven", "rejected", "DWO 4.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(actual)!);
        await File.WriteAllBytesAsync(actual, new byte[] { 7 });

        var path = await _storage.MoveFileToStatusAsync("De Wolven", stale, SubmissionStatus.Approved);

        path.Should().Be(Path.Combine(_temp.Path, "de-wolven", "approved", "DWO 4.jpg"));
        (await File.ReadAllBytesAsync(path)).Should().Equal(7);
        File.Exists(actual).Should().BeFalse();
    }

    [Fact]
    public async Task MoveFileToStatusAsync_SameStatus_ShouldKeepPath()
    {
        var path = await _storage.SaveInitialFileAsync("De Wolven", "a.jpg", new MemoryStream(new byte[] { 1 }));

        var moved = await _storage.MoveFileToStatusAsync("De Wolven", path, SubmissionStatus.Pending);

        moved.Should().Be(path);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task MoveFileToStatusAsync_TakenName_ShouldAddCounter()
    {
        var first = await _storage.SaveInitialFileAsync("De Wolven", "CDW 1.jpg", new MemoryStream(new byte[] { 1 }));
        await _storage.MoveFileToStatusAsync("De Wolven", first, SubmissionStatus.Approved);
        var second = await _storage.SaveInitialFileAsync("De Wolven", "CDW 1.jpg", new MemoryStream(new byte[] { 2 }));

        var moved = await _storage.MoveFileToStatusAsync("De Wolven", second, SubmissionStatus.Approved);

        Path.GetFileName(moved).Should().Be("CDW 1 (2).jpg");
    }

    [Fact]
    public async Task MoveFileToStatusAsync_SecondRejectedAttempt_ShouldKeepBothFiles()
    {
        var first = await _storage.SaveInitialFileAsync("De Wolven", "CDW 1 10-00-00.jpg", new MemoryStream(new byte[] { 1 }));
        var firstRejected = await _storage.MoveFileToStatusAsync("De Wolven", first, SubmissionStatus.Rejected);
        var second = await _storage.SaveInitialFileAsync("De Wolven", "CDW 1 10-00-00.jpg", new MemoryStream(new byte[] { 2 }));

        var secondRejected = await _storage.MoveFileToStatusAsync("De Wolven", second, SubmissionStatus.Rejected);

        var rejectedDir = _storage.GetStatusDirectoryPath("De Wolven", SubmissionStatus.Rejected);
        Directory.GetFiles(rejectedDir).Select(Path.GetFileName)
            .Should().BeEquivalentTo("CDW 1 10-00-00.jpg", "CDW 1 10-00-00 (2).jpg");
        File.ReadAllBytes(firstRejected).Should().Equal(1);
        File.ReadAllBytes(secondRejected).Should().Equal(2);
    }

    [Fact]
    public void GetStatusDirectoryPath_ShouldSanitizeGroupName()
    {
        var path = _storage.GetStatusDirectoryPath("De Wolven\0 A", SubmissionStatus.Received);

        Path.GetFileName(path).Should().Be("de-wolven_-a");
    }

    [Fact]
    public void Constructor_WithoutStorageRoot_ShouldUseDefaultFolder()
    {
        // The working folder is shared by all tests running in parallel, so it is read but never changed
        var defaultRoot = Path.GetFullPath("./scouting_storage");
        var existed = Directory.Exists(defaultRoot);
        try
        {
            var storage = new LocalFileStorageService(Options.Create(new ScoutingSettings { StorageRoot = "" }), NullLogger<LocalFileStorageService>.Instance);

            storage.GetStatusDirectoryPath("g", SubmissionStatus.Pending).Should().Be(Path.Combine(defaultRoot, "g"));
        }
        finally
        {
            if (!existed) Directory.Delete(defaultRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../buiten")]
    [InlineData("..\\buiten")]
    [InlineData("a/../../b")]
    [InlineData("   ")]
    public void GetStatusDirectoryPath_TraversingGroupName_ShouldStayInsideRoot(string groupName)
    {
        foreach (var status in Enum.GetValues<SubmissionStatus>())
        {
            var path = _storage.GetStatusDirectoryPath(groupName, status);

            path.Should().StartWith(Path.GetFullPath(_temp.Path) + Path.DirectorySeparatorChar);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task SaveInitialFileAsync_DotName_ShouldGenerateOne(string fileName)
    {
        var path = await _storage.SaveInitialFileAsync("De Wolven", fileName, new MemoryStream(new byte[] { 1 }));

        Path.GetFileName(path).Should().StartWith("file_");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ReadingOutsideRoot_ShouldBeRefused()
    {
        using var outside = new TempDirectory();
        var secret = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(secret, "geheim");
        var relative = Path.Combine(_temp.Path, "..", Path.GetFileName(outside.Path), "secret.txt");

        foreach (var path in new[] { secret, relative })
        {
            (await _storage.FileExistsAsync(path)).Should().BeFalse();
            (await _storage.OpenReadStreamAsync(path)).Should().BeNull();
        }
    }

    [Fact]
    public async Task MoveFileToStatusAsync_SourceOutsideRoot_ShouldThrowAndKeepTheFile()
    {
        using var outside = new TempDirectory();
        var secret = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(secret, "geheim");

        var act = () => _storage.MoveFileToStatusAsync("De Wolven", secret, SubmissionStatus.Approved);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        File.Exists(secret).Should().BeTrue();
        Directory.GetFiles(_temp.Path, "secret.txt", SearchOption.AllDirectories).Should().BeEmpty();
    }

    public void Dispose() => _temp.Dispose();
}
