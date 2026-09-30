using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Infrastructure.Services;

public static class SubmissionFileName
{
    /// <summary>
    /// Builds the stored file name "{abbreviation} {assignment number}{extension}", e.g. "CDW 16.jpg".
    /// Without an assignment number the original name is kept after the abbreviation.
    /// Falls back to the group name when no abbreviation is configured.
    /// </summary>
    public static string Build(Group group, int? assignmentNumber, string originalFileName)
    {
        var prefix = string.IsNullOrWhiteSpace(group.Abbreviation) ? group.Name : group.Abbreviation;
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var suffix = assignmentNumber.HasValue
            ? assignmentNumber.Value.ToString()
            : Path.GetFileNameWithoutExtension(originalFileName);

        return RemoveInvalidCharacters($"{prefix.Trim()} {suffix.Trim()}") + extension;
    }

    // Windows-invalid characters are removed on every OS so stored files stay portable
    private static readonly HashSet<char> InvalidCharacters =
        new(Path.GetInvalidFileNameChars().Concat("\\/:*?\"<>|"));

    private static string RemoveInvalidCharacters(string name)
    {
        var cleaned = new string(name.Where(c => !InvalidCharacters.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? $"file_{Guid.NewGuid():N}" : cleaned;
    }
}
