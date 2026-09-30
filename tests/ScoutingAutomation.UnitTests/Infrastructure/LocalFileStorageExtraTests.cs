using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Infrastructure;

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
    public async Task MoveFileToStatusAsync_MissingSource_ShouldCreatePlaceholder()
    {
        var missing = Path.Combine(_temp.Path, "de-wolven", "weg.jpg");

        var path = await _storage.MoveFileToStatusAsync("De Wolven", missing, SubmissionStatus.Approved);

        path.Should().Be(Path.Combine(_temp.Path, "de-wolven", "approved", "weg.jpg"));
        File.ReadAllText(path).Should().Contain("weg.jpg");
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

    public void Dispose() => _temp.Dispose();
}
