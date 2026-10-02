using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>Writes and reads the app's small settings files so that a crash or a bad file never loses them.</summary>
internal static class DurableFile
{
    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/> in one step: written to a temporary file,
    /// flushed to the disk, then moved over the old file. A power cut leaves the old or the new file, never half of one.
    /// </summary>
    /// <param name="ownerOnly">Readable by the app's own user only, for a file with secrets.</param>
    public static void WriteAllText(string path, string contents, bool ownerOnly = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (ownerOnly && !OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Makes an existing file readable by the app's own user only; on Windows this does nothing.</summary>
    public static void RestrictToOwner(string path, ILogger logger)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not make {File} readable by the app only", path);
        }
    }

    /// <summary>
    /// Renames a file that could not be read to "{name}.corrupt-{time}", so it is kept for repair
    /// and the next save does not overwrite it.
    /// </summary>
    /// <returns>The new path, or null when it could not be renamed.</returns>
    public static string? Quarantine(string path, ILogger logger)
    {
        try
        {
            var target = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            File.Move(path, target, overwrite: false);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not set the unreadable file {File} aside", path);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next save
        }
    }
}
