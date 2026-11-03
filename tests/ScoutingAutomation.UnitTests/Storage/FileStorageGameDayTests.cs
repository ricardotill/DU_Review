using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Storage;
using Xunit;

namespace ScoutingAutomation.UnitTests.Storage;

public class FileStorageGameDayTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly LocalFileStorageService _storage;

    public FileStorageGameDayTests()
    {
        _storage = new LocalFileStorageService(Options.Create(new ScoutingSettings { StorageRoot = _temp.Path }), NullLogger<LocalFileStorageService>.Instance);
    }

    private string GroupDir => Path.Combine(_temp.Path, "de-wolven");

    [Fact]
    public async Task SaveInitialFile_WhenTheContentFails_ShouldLeaveNothingBehind()
    {
        var act = () => _storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new BrokenStream());

        await act.Should().ThrowAsync<IOException>();
        Directory.GetFiles(GroupDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveInitialFile_NameTaken_ShouldAddANumber()
    {
        var first = await _storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new MemoryStream(new byte[] { 1 }));
        var second = await _storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new MemoryStream(new byte[] { 2 }));

        Path.GetFileName(first).Should().Be("DWO 4.jpg");
        Path.GetFileName(second).Should().Be("DWO 4 (2).jpg");
        Directory.GetFiles(GroupDir).Should().HaveCount(2, "no temporary file is left");
    }

    [Fact]
    public async Task SavesAtTheSameTime_ShouldEachGetTheirOwnFile()
    {
        var paths = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => _storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new MemoryStream(new[] { (byte)i })))));

        paths.Distinct().Should().HaveCount(10);
        paths.Select(p => File.ReadAllBytes(p).Single()).Should().BeEquivalentTo(Enumerable.Range(0, 10).Select(i => (byte)i));
    }

    [Fact]
    public async Task RenameFile_ShouldKeepTheFolder()
    {
        var path = await _storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new MemoryStream(new byte[] { 1 }));
        var approved = await _storage.MoveFileToStatusAsync("De Wolven", path, SubmissionStatus.Approved);

        var renamed = await _storage.RenameFileAsync(approved, "DWO 7.jpg");

        renamed.Should().Be(Path.Combine(GroupDir, "approved", "DWO 7.jpg"));
        File.Exists(renamed).Should().BeTrue();
        File.Exists(approved).Should().BeFalse();
    }

    [Fact]
    public async Task MoveToDeleted_ShouldTakeTheFileOutOfTheGroupFolder()
    {
        var path = await _storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new MemoryStream(new byte[] { 1 }));

        var moved = await _storage.MoveToDeletedAsync("De Wolven", path);

        moved.Should().Be(Path.Combine(_temp.Path, "_deleted", "de-wolven", "DWO 4.jpg"));
        File.Exists(path).Should().BeFalse();
        (await _storage.MoveToDeletedAsync("De Wolven", path)).Should().BeNull("the file is already gone");
    }

    [Fact]
    public async Task BackupForRebuild_ShouldLeaveDeletedFilesAlone()
    {
        var path = await _storage.SaveInitialFileAsync("De Wolven", "DWO 4.jpg", new MemoryStream(new byte[] { 1 }));
        await _storage.MoveToDeletedAsync("De Wolven", path);

        await _storage.MoveAllToBackupAsync(new[] { "De Wolven" });

        Directory.Exists(Path.Combine(_temp.Path, "_deleted", "de-wolven")).Should().BeTrue();
    }

    [Fact]
    public async Task Backup_ShouldLeaveTheAssignmentPicturesAlone()
    {
        // A rebuild from Slack replaces the group folders; the pictures do not come from Slack
        var picture = await _storage.SaveAssignmentImageAsync("12", "voorbeeld.png", new MemoryStream(new byte[] { 1 }));

        await _storage.MoveAllToBackupAsync(new[] { "De Wolven" });

        File.Exists(picture).Should().BeTrue();
        picture.Replace('\\', '/').Should().Contain("/_opdrachten/12/");
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("012")]
    public async Task AssignmentPicture_CodeThatIsNotNormalised_ShouldBeRefused(string code)
    {
        var act = () => _storage.SaveAssignmentImageAsync(code, "a.png", new MemoryStream(new byte[] { 1 }));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose() => _temp.Dispose();

    private sealed class BrokenStream : MemoryStream
    {
        public BrokenStream() : base(new byte[] { 1 }) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("connection reset");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new IOException("connection reset");
    }
}
