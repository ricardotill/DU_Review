using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Interfaces;

public interface IFileStorageService
{
    Task<string> SaveInitialFileAsync(string groupName, string fileName, Stream contentStream, CancellationToken ct = default);
    Task<string> MoveFileToStatusAsync(string groupName, string currentStoragePath, SubmissionStatus targetStatus, CancellationToken ct = default);
    Task<bool> FileExistsAsync(string storagePath, CancellationToken ct = default);
    Task<Stream?> OpenReadStreamAsync(string storagePath, CancellationToken ct = default);
    Task EnsureFolderStructureAsync(string groupName, CancellationToken ct = default);
    string GetStatusDirectoryPath(string groupName, SubmissionStatus status);

    /// <summary>Moves every stored file into a new timestamped backup folder and returns its path.</summary>
    Task<string> MoveAllToBackupAsync(CancellationToken ct = default);
}
