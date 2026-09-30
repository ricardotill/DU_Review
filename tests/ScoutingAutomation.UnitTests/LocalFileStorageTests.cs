using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests;

public class LocalFileStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFileStorageService _storageService;

    public LocalFileStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ScoutingTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var options = Options.Create(new ScoutingSettings
        {
            StorageRoot = _tempDir
        });

        _storageService = new LocalFileStorageService(options, NullLogger<LocalFileStorageService>.Instance);
    }

    [Fact]
    public async Task EnsureFolderStructureAsync_ShouldCreateAllStatusFolders()
    {
        // Act
        await _storageService.EnsureFolderStructureAsync("De Zeearenden");

        // Assert
        var expectedGroupPath = Path.Combine(_tempDir, "de-zeearenden");
        Directory.Exists(expectedGroupPath).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedGroupPath, "pending")).Should().BeFalse();
        Directory.Exists(Path.Combine(expectedGroupPath, "approved")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedGroupPath, "rejected")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedGroupPath, "awesome")).Should().BeTrue();
    }

    [Fact]
    public async Task SaveInitialFileAsync_ShouldSaveToGroupFolder()
    {
        // Arrange
        const string groupName = "De Bosuilen";
        const string fileName = "vuur.jpg";
        const string fileContent = "Simulated image data";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));

        // Act
        var savedPath = await _storageService.SaveInitialFileAsync(groupName, fileName, stream);

        // Assert
        File.Exists(savedPath).Should().BeTrue();
        Path.GetDirectoryName(savedPath).Should().Be(Path.Combine(_tempDir, "de-bosuilen"));
        var content = await File.ReadAllTextAsync(savedPath);
        content.Should().Be(fileContent);
    }

    [Fact]
    public async Task MoveFileToStatusAsync_ShouldMoveFileBetweenStatusFolders()
    {
        // Arrange
        const string groupName = "De Wolven";
        const string fileName = "vlaggenmast.png";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Test bytes"));
        var initialPendingPath = await _storageService.SaveInitialFileAsync(groupName, fileName, stream);

        // Act - Move to Approved
        var approvedPath = await _storageService.MoveFileToStatusAsync(groupName, initialPendingPath, SubmissionStatus.Approved);

        // Assert
        File.Exists(initialPendingPath).Should().BeFalse();
        File.Exists(approvedPath).Should().BeTrue();
        approvedPath.Should().Contain(Path.Combine("de-wolven", "approved"));

        // Act - Move to Awesome
        var awesomePath = await _storageService.MoveFileToStatusAsync(groupName, approvedPath, SubmissionStatus.Awesome);

        // Assert
        File.Exists(approvedPath).Should().BeFalse();
        File.Exists(awesomePath).Should().BeTrue();
        awesomePath.Should().Contain(Path.Combine("de-wolven", "awesome"));
    }

    [Fact]
    public async Task SaveInitialFileAsync_SameName_ShouldAddCounter()
    {
        // Act
        var first = await _storageService.SaveInitialFileAsync("De Wolven", "CDW 16.jpg", new MemoryStream(new byte[] { 1 }));
        var second = await _storageService.SaveInitialFileAsync("De Wolven", "CDW 16.jpg", new MemoryStream(new byte[] { 2 }));
        var third = await _storageService.SaveInitialFileAsync("De Wolven", "CDW 16.jpg", new MemoryStream(new byte[] { 3 }));

        // Assert
        Path.GetFileName(first).Should().Be("CDW 16.jpg");
        Path.GetFileName(second).Should().Be("CDW 16 (2).jpg");
        Path.GetFileName(third).Should().Be("CDW 16 (3).jpg");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors
        }
    }
}
