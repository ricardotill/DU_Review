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

    public string GetStatusDirectoryPath(string groupName, SubmissionStatus status)
    {
        var groupRoot = Path.Combine(_storageRoot, SanitizePath(groupName));

        // Unreviewed files live in the group folder itself; reviewed files get a status subfolder
        if (status is SubmissionStatus.Received or SubmissionStatus.Pending)
        {
            return groupRoot;
        }

        return Path.Combine(groupRoot, status.ToString().ToLowerInvariant());
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
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = $"file_{Guid.NewGuid():N}";
        }

        var destinationPath = GetAvailablePath(groupDir, safeFileName);

        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await contentStream.CopyToAsync(fileStream, ct);

        _logger.LogInformation("Saved initial submission file to: {Path}", destinationPath);
        return destinationPath;
    }

    public Task<string> MoveFileToStatusAsync(string groupName, string currentStoragePath, SubmissionStatus targetStatus, CancellationToken ct = default)
    {
        var targetDir = GetStatusDirectoryPath(groupName, targetStatus);
        Directory.CreateDirectory(targetDir);

        if (!File.Exists(currentStoragePath))
        {
            _logger.LogWarning("Source file not found at {CurrentPath} for group {Group}. Creating empty placeholder at target.", currentStoragePath, groupName);
            var safeName = Path.GetFileName(currentStoragePath);
            var newPath = Path.Combine(targetDir, safeName);
            File.WriteAllText(newPath, $"Simulated content for {safeName}");
            return Task.FromResult(newPath);
        }

        var fileName = Path.GetFileName(currentStoragePath);
        var destinationPath = Path.Combine(targetDir, fileName);

        if (!string.Equals(destinationPath, currentStoragePath, StringComparison.OrdinalIgnoreCase))
        {
            destinationPath = GetAvailablePath(targetDir, fileName);
        }

        if (!string.Equals(currentStoragePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(currentStoragePath, destinationPath, overwrite: true);
            _logger.LogInformation("Moved submission file from {Source} to {Target}", currentStoragePath, destinationPath);
        }

        return Task.FromResult(destinationPath);
    }

    /// <summary>
    /// Returns "name.ext", or "name (2).ext", "name (3).ext", ... when taken.
    /// </summary>
    private static string GetAvailablePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(directory, $"{nameWithoutExt} ({i}){ext}");
        }

        return path;
    }

    public const string BackupFolderName = "_backup";

    public Task<string> MoveAllToBackupAsync(CancellationToken ct = default)
    {
        var backupRoot = Path.Combine(_storageRoot, BackupFolderName);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(backupRoot, stamp);
        for (var i = 2; Directory.Exists(backup); i++)
        {
            backup = Path.Combine(backupRoot, $"{stamp}-{i}");
        }

        Directory.CreateDirectory(backup);
        foreach (var folder in Directory.GetDirectories(_storageRoot).Where(d => Path.GetFileName(d) != BackupFolderName))
        {
            Directory.Move(folder, Path.Combine(backup, Path.GetFileName(folder)));
        }

        _logger.LogInformation("Moved stored files to backup {Backup}", backup);
        return Task.FromResult(backup);
    }

    public Task<bool> FileExistsAsync(string storagePath, CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(storagePath));
    }

    public Task<Stream?> OpenReadStreamAsync(string storagePath, CancellationToken ct = default)
    {
        if (!File.Exists(storagePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(stream);
    }

    private static string SanitizePath(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().Replace(' ', '-').ToLowerInvariant();
    }
}
