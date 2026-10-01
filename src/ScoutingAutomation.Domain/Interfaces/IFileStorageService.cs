using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Interfaces;

/// <summary>
/// Stores submission files under one storage root. Every path it returns is inside that root.
/// Paths outside the root are refused: reads return null or false, writes and moves throw
/// <see cref="UnauthorizedAccessException"/>.
/// </summary>
public interface IFileStorageService
{
    Task<string> SaveInitialFileAsync(string groupName, string fileName, Stream contentStream, CancellationToken ct = default);
    Task<string> MoveFileToStatusAsync(string groupName, string currentStoragePath, SubmissionStatus targetStatus, CancellationToken ct = default);
    /// <summary>Gives a stored file a new name in the same folder; adds " (2)" etc. when the name is taken.</summary>
    /// <returns>The new path, or the current path when the file does not exist.</returns>
    Task<string> RenameFileAsync(string storagePath, string newFileName, CancellationToken ct = default);

    /// <summary>Moves a file out of the group folders into "_deleted/{group}", so it no longer counts but can be recovered.</summary>
    /// <returns>The new path, or null when the file does not exist.</returns>
    Task<string?> MoveToDeletedAsync(string groupName, string storagePath, CancellationToken ct = default);

    /// <summary>Removes a stored file. Does nothing when it does not exist.</summary>
    Task DeleteFileAsync(string storagePath, CancellationToken ct = default);

    Task<bool> FileExistsAsync(string storagePath, CancellationToken ct = default);
    Task<Stream?> OpenReadStreamAsync(string storagePath, CancellationToken ct = default);
    Task EnsureFolderStructureAsync(string groupName, CancellationToken ct = default);
    string GetStatusDirectoryPath(string groupName, SubmissionStatus status);

    /// <summary>Moves every stored file into a new timestamped backup folder and returns its path.</summary>
    Task<string> MoveAllToBackupAsync(CancellationToken ct = default);

    /// <summary>Moves one group's stored files into a new timestamped backup folder and returns its path.</summary>
    Task<string> MoveGroupToBackupAsync(string groupName, CancellationToken ct = default);
}
