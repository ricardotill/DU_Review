using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Storage;

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
        RemoveLeftoverPartFiles();
    }

    /// <summary>
    /// A download that was cut off by a crash leaves its ".part" file behind. Nothing can be in progress yet
    /// when the storage starts, so every one of them is a leftover.
    /// </summary>
    private void RemoveLeftoverPartFiles()
    {
        try
        {
            foreach (var part in Directory.EnumerateFiles(_storageRoot, ".*.part", SearchOption.AllDirectories))
            {
                File.Delete(part);
                _logger.LogInformation("Removed unfinished download {Path}", part);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove unfinished downloads under {Root}", _storageRoot);
        }
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

    /// <summary>The folder inside a group folder for the Avonturiers' files, with its own status folders.</summary>
    public const string AdventurersFolderName = "avonturiers";

    private string AdventurersDirectoryPath(string groupName, SubmissionStatus status)
    {
        var root = Path.Combine(GetStatusDirectoryPath(groupName, SubmissionStatus.Pending), AdventurersFolderName);
        return EnsureInsideRoot(status is SubmissionStatus.Received or SubmissionStatus.Pending
            ? root
            : Path.Combine(root, status.ToString().ToLowerInvariant()));
    }

    /// <summary>Whether a stored file is in the group's Avonturiers folder (or one of its status folders).</summary>
    private bool IsAdventurersFile(string groupName, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(AdventurersDirectoryPath(groupName, SubmissionStatus.Pending)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, PathComparison);
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

    public Task<string> SaveInitialFileAsync(string groupName, string fileName, Stream contentStream, CancellationToken ct = default) =>
        SaveAsync(GetStatusDirectoryPath(groupName, SubmissionStatus.Pending), fileName, contentStream, ct);

    public Task<string> SaveAdventurersFileAsync(string groupName, string fileName, Stream contentStream, CancellationToken ct = default) =>
        SaveAsync(AdventurersDirectoryPath(groupName, SubmissionStatus.Pending), fileName, contentStream, ct);

    /// <summary>The folder of the assignments' pictures; starts with "_", so no group folder can have its name.</summary>
    public const string AssignmentImagesFolderName = "_opdrachten";

    public Task<string> SaveAssignmentImageAsync(string assignmentCode, string fileName, Stream contentStream, CancellationToken ct = default)
    {
        // The code is normalised by the caller; anything else could point outside the folder
        if (AssignmentCode.Normalize(assignmentCode) != assignmentCode) throw new ArgumentException($"'{assignmentCode}' is no assignment code", nameof(assignmentCode));

        return SaveAsync(Path.Combine(_storageRoot, AssignmentImagesFolderName, assignmentCode), fileName, contentStream, ct);
    }

    private async Task<string> SaveAsync(string groupDir, string fileName, Stream contentStream, CancellationToken ct)
    {
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
        var targetDir = IsAdventurersFile(groupName, currentStoragePath)
            ? AdventurersDirectoryPath(groupName, targetStatus)
            : GetStatusDirectoryPath(groupName, targetStatus);
        Directory.CreateDirectory(targetDir);

        if (!File.Exists(currentStoragePath))
        {
            // Never write a stand-in: it could overwrite a real file, and would hide that the file is gone.
            // A file moved by an earlier step that did not get saved is found again in another status folder.
            var found = FindInGroupFolders(groupName, Path.GetFileName(currentStoragePath), IsAdventurersFile(groupName, currentStoragePath));
            if (found == null)
            {
                _logger.LogError("Submission file {Path} of group {Group} is missing; the status changes, the file cannot follow", currentStoragePath, groupName);
                return Task.FromResult(currentStoragePath);
            }

            _logger.LogWarning("Submission file {Path} was found at {Found}", currentStoragePath, found);
            currentStoragePath = found;
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

    /// <summary>The file with this exact name in one of the group's status folders, or null.</summary>
    private string? FindInGroupFolders(string groupName, string fileName, bool adventurers) =>
        Enum.GetValues<SubmissionStatus>()
            .Select(status => Path.Combine(adventurers ? AdventurersDirectoryPath(groupName, status) : GetStatusDirectoryPath(groupName, status), fileName))
            .FirstOrDefault(File.Exists);

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

    public Task<string> MoveToPathAsync(string storagePath, string targetPath, CancellationToken ct = default)
    {
        var currentPath = EnsureInsideRoot(storagePath);
        var target = EnsureInsideRoot(targetPath);
        if (string.Equals(currentPath, target, PathComparison)) return Task.FromResult(target);

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        return Task.FromResult(MoveToAvailablePath(currentPath, directory, Path.GetFileName(target)));
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

    public Task<string> MoveAllToBackupAsync(IReadOnlyCollection<string> groupNames, CancellationToken ct = default)
    {
        var backup = CreateBackupFolder();
        // Only the app's own group folders: the storage root may be a shared folder with other things in it
        foreach (var folder in groupNames.Select(FolderName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var source = EnsureInsideRoot(Path.Combine(_storageRoot, folder));
            if (Directory.Exists(source))
            {
                Directory.Move(source, Path.Combine(backup, folder));
            }
        }

        _logger.LogInformation("Moved stored files to backup {Backup}", backup);
        return Task.FromResult(backup);
    }

    public Task RestoreBackupAsync(string backupFolder, CancellationToken ct = default)
    {
        var backup = EnsureInsideRoot(backupFolder);
        foreach (var folder in Directory.GetDirectories(backup))
        {
            var target = Path.Combine(_storageRoot, Path.GetFileName(folder));
            if (Directory.Exists(target))
            {
                // Recreated after the move: only empty status folders can be in it yet
                if (Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any())
                {
                    _logger.LogError("Cannot restore {Folder} from backup {Backup}: the folder has files again", target, backup);
                    continue;
                }

                Directory.Delete(target, recursive: true);
            }

            Directory.Move(folder, target);
        }

        _logger.LogWarning("Restored stored files from backup {Backup}", backup);
        return Task.CompletedTask;
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

        // FileShare.Delete: on Windows, a review may move the file while a video plays or an export reads it
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
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

    private static string SanitizePath(string input) => FolderName(input);

    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    // Characters Windows refuses, also on other systems, so the storage can be copied to a Windows share
    private static readonly HashSet<char> InvalidFolderCharacters = new(Path.GetInvalidFileNameChars().Concat("\\/:*?\"<>|"));

    /// <summary>
    /// Turns a group name into one folder name. It can never be ".", "..", contain separators, start with "_"
    /// (the app's own "_backup" and "_deleted" folders), end in a dot or space, or be a Windows device name.
    /// </summary>
    public static string FolderName(string groupName)
    {
        var chars = groupName.Select(c => InvalidFolderCharacters.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim().Replace(' ', '-').ToLowerInvariant().TrimStart('.').TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) return "group";
        if (name.StartsWith('_')) name = "group" + name;
        if (WindowsDeviceNames.Contains(name)) name += "_";
        return name;
    }
}
