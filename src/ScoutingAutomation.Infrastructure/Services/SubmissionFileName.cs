using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Infrastructure.Services;

public static class SubmissionFileName
{
    /// <summary>
    /// Builds the stored file name "{abbreviation} {assignment number} {time}{extension}", e.g. "CDW 16 14-32-05.jpg".
    /// The time tells several attempts at one assignment apart; the game takes one day, so no date is needed.
    /// <paramref name="postedAt"/> is written as given, so pass it in the time zone the files are read in.
    /// Without an assignment number the original name is kept after the abbreviation.
    /// Falls back to the group name when no abbreviation is configured.
    /// <paramref name="index"/> numbers the files of a message with several files: "CDW 16 14-32-05 (2).jpg".
    /// </summary>
    public static string Build(Group group, int? assignmentNumber, string originalFileName, DateTimeOffset postedAt, int? index = null)
    {
        var prefix = string.IsNullOrWhiteSpace(group.Abbreviation) ? group.Name : group.Abbreviation;
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var suffix = assignmentNumber.HasValue
            ? assignmentNumber.Value.ToString()
            : Path.GetFileNameWithoutExtension(originalFileName);

        var number = index.HasValue ? $" ({index.Value})" : "";
        return RemoveInvalidCharacters($"{prefix.Trim()} {suffix.Trim()} {postedAt:HH-mm-ss}{number}") + extension;
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
