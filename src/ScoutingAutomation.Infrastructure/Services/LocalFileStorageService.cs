using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;

namespace ScoutingAutomation.Infrastructure.Services;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _storageRoot;
    private readonly ILogger<LocalFileStorageService> _logger;

    public LocalFileStorageService(IOptions<ScoutingSettings> options, ILogger<LocalFileStorageService> logger)
    {
        _logger = logger;
        _storageRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.StorageRoot)
            ? "./scouting_storage"
            : options.Value.StorageRoot);

        Directory.CreateDirectory(_storageRoot);
    }

    // macOS and Windows file systems ignore case by default, Linux does not
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public string GetStatusDirectoryPath(string groupName, SubmissionStatus status)
    {
        var groupRoot = Path.Combine(_storageRoot, SanitizePath(groupName));

        // Unreviewed files live in the group folder itself; reviewed files get a status subfolder
        if (status is SubmissionStatus.Received or SubmissionStatus.Pending)
        {
            return EnsureInsideRoot(groupRoot);
        }

        return EnsureInsideRoot(Path.Combine(groupRoot, status.ToString().ToLowerInvariant()));
    }

    public Task EnsureFolderStructureAsync(string groupName, CancellationToken ct = default)
    {
        var safeGroupName = SanitizePath(groupName);
        var groupRoot = Path.Combine(_storageRoot, safeGroupName);

        foreach (var status in Enum.GetValues<SubmissionStatus>())
        {
            Directory.CreateDirectory(GetStatusDirectoryPath(groupName, status));
        }

        return Task.CompletedTask;
    }

    public async Task<string> SaveInitialFileAsync(string groupName, string fileName, Stream contentStream, CancellationToken ct = default)
    {
        var groupDir = GetStatusDirectoryPath(groupName, SubmissionStatus.Pending);
        Directory.CreateDirectory(groupDir);

        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName is "." or "..")
        {
            safeFileName = $"file_{Guid.NewGuid():N}";
        }

        // Written under a temporary name first: a download that fails halfway never leaves a broken file
        var partPath = EnsureInsideRoot(Path.Combine(groupDir, $".{Guid.NewGuid():N}.part"));
        try
        {
            await using (var fileStream = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await contentStream.CopyToAsync(fileStream, ct);
            }

            var destinationPath = MoveToAvailablePath(partPath, groupDir, safeFileName);
            _logger.LogInformation("Saved initial submission file to: {Path}", destinationPath);
            return destinationPath;
        }
        catch
        {
            File.Delete(partPath);
            throw;
        }
    }

    /// <summary>Moves a file to the first free name, also when another save claims a name at the same moment.</summary>
    private string MoveToAvailablePath(string sourcePath, string directory, string fileName)
    {
        while (true)
        {
            var destinationPath = GetAvailablePath(directory, fileName);
            try
            {
                // Creating the file claims the name in one step; a move alone could replace a file saved at the same moment
                new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Taken in the meantime: try the next number
                continue;
            }

            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
            }
            catch
            {
                File.Delete(destinationPath);
                throw;
            }

            return destinationPath;
        }
    }

    public Task<string> MoveFileToStatusAsync(string groupName, string currentStoragePath, SubmissionStatus targetStatus, CancellationToken ct = default)
    {
        currentStoragePath = EnsureInsideRoot(currentStoragePath);
        var targetDir = GetStatusDirectoryPath(groupName, targetStatus);
        Directory.CreateDirectory(targetDir);

        if (!File.Exists(currentStoragePath))
        {
            _logger.LogWarning("Source file not found at {CurrentPath} for group {Group}. Creating empty placeholder at target.", currentStoragePath, groupName);
            var safeName = Path.GetFileName(currentStoragePath);
            var newPath = EnsureInsideRoot(Path.Combine(targetDir, safeName));
            File.WriteAllText(newPath, $"Simulated content for {safeName}");
            return Task.FromResult(newPath);
        }

        var fileName = Path.GetFileName(currentStoragePath);
        var destinationPath = Path.Combine(targetDir, fileName);

        if (!string.Equals(destinationPath, currentStoragePath, PathComparison))
        {
            destinationPath = MoveToAvailablePath(currentStoragePath, targetDir, fileName);
            _logger.LogInformation("Moved submission file from {Source} to {Target}", currentStoragePath, destinationPath);
        }

        return Task.FromResult(destinationPath);
    }

    public Task<string> RenameFileAsync(string storagePath, string newFileName, CancellationToken ct = default)
    {
        var currentPath = EnsureInsideRoot(storagePath);
        var safeFileName = Path.GetFileName(newFileName);
        var directory = Path.GetDirectoryName(currentPath)!;
        if (!File.Exists(currentPath) || string.IsNullOrWhiteSpace(safeFileName)
            || string.Equals(Path.Combine(directory, safeFileName), currentPath, PathComparison))
        {
            return Task.FromResult(currentPath);
        }

        var newPath = MoveToAvailablePath(currentPath, directory, safeFileName);
        _logger.LogInformation("Renamed submission file {Source} to {Target}", currentPath, newPath);
        return Task.FromResult(newPath);
    }

    public const string DeletedFolderName = "_deleted";

    public Task<string?> MoveToDeletedAsync(string groupName, string storagePath, CancellationToken ct = default)
    {
        var currentPath = EnsureInsideRoot(storagePath);
        if (!File.Exists(currentPath))
        {
            return Task.FromResult<string?>(null);
        }

        var deletedDir = EnsureInsideRoot(Path.Combine(_storageRoot, DeletedFolderName, SanitizePath(groupName)));
        Directory.CreateDirectory(deletedDir);
        var newPath = MoveToAvailablePath(currentPath, deletedDir, Path.GetFileName(currentPath));
        _logger.LogInformation("Moved deleted submission file {Source} to {Target}", currentPath, newPath);
        return Task.FromResult<string?>(newPath);
    }

    public Task DeleteFileAsync(string storagePath, CancellationToken ct = default)
    {
        File.Delete(EnsureInsideRoot(storagePath));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns "name.ext", or "name (2).ext", "name (3).ext", ... when taken.
    /// </summary>
    private string GetAvailablePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(directory, $"{nameWithoutExt} ({i}){ext}");
        }

        return EnsureInsideRoot(path);
    }

    public const string BackupFolderName = "_backup";

    public Task<string> MoveAllToBackupAsync(CancellationToken ct = default)
    {
        var backup = CreateBackupFolder();
        foreach (var folder in Directory.GetDirectories(_storageRoot).Where(d => Path.GetFileName(d) is not (BackupFolderName or DeletedFolderName)))
        {
            Directory.Move(folder, Path.Combine(backup, Path.GetFileName(folder)));
        }

        _logger.LogInformation("Moved stored files to backup {Backup}", backup);
        return Task.FromResult(backup);
    }

    public Task<string> MoveGroupToBackupAsync(string groupName, CancellationToken ct = default)
    {
        var groupRoot = GetStatusDirectoryPath(groupName, SubmissionStatus.Pending);
        var backup = CreateBackupFolder();
        if (Directory.Exists(groupRoot))
        {
            Directory.Move(groupRoot, Path.Combine(backup, Path.GetFileName(groupRoot)));
        }

        _logger.LogInformation("Moved stored files of group {Group} to backup {Backup}", groupName, backup);
        return Task.FromResult(backup);
    }

    private string CreateBackupFolder()
    {
        var backupRoot = Path.Combine(_storageRoot, BackupFolderName);
        var stamp = DateTimeOffset.UtcNow.ToGameTime().ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(backupRoot, stamp);
        for (var i = 2; Directory.Exists(backup); i++)
        {
            backup = Path.Combine(backupRoot, $"{stamp}-{i}");
        }

        backup = EnsureInsideRoot(backup);
        Directory.CreateDirectory(backup);
        return backup;
    }

    public Task<bool> FileExistsAsync(string storagePath, CancellationToken ct = default)
    {
        return Task.FromResult(IsInsideRoot(storagePath, out var fullPath) && File.Exists(fullPath));
    }

    public Task<Stream?> OpenReadStreamAsync(string storagePath, CancellationToken ct = default)
    {
        if (!IsInsideRoot(storagePath, out var fullPath) || !File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(stream);
    }

    /// <summary>
    /// Whether the path points inside the storage root. Paths are compared after resolving "..",
    /// so "root/../secret.txt" is outside. Symbolic links inside the root are not followed.
    /// </summary>
    private bool IsInsideRoot(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(_storageRoot) + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(root, PathComparison)) return true;

        _logger.LogWarning("Refused path outside the storage root: {Path}", path);
        return false;
    }

    /// <returns>The full path.</returns>
    /// <exception cref="UnauthorizedAccessException">The path points outside the storage root.</exception>
    private string EnsureInsideRoot(string path) =>
        IsInsideRoot(path, out var fullPath)
            ? fullPath
            : throw new UnauthorizedAccessException($"Path '{path}' is outside the storage root.");

    /// <summary>Turns a group name into one folder name; it can never be ".", ".." or contain separators.</summary>
    private static string SanitizePath(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var name = new string(chars).Trim().Replace(' ', '-').ToLowerInvariant().TrimStart('.');
        return string.IsNullOrWhiteSpace(name) ? "_" : name;
    }
}
