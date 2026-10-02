using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Reads assignment points from a CSV with the header "Opdracht,standaard,awesome".
/// Commas or semicolons both work (Dutch Excel saves with semicolons), and empty fields at the end of a line are ignored.
/// </summary>
public static class AssignmentPointsCsvReader
{
    public static IReadOnlyList<Assignment> Read(TextReader reader)
    {
        var assignments = new List<Assignment>();
        var seenNumbers = new HashSet<int>();
        var lineNumber = 0;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = Split(line);

            // The first line is the header, unless it already holds an assignment: then nothing may be skipped
            if (lineNumber == 1 && !int.TryParse(parts[0], out _))
            {
                continue;
            }

            if (parts.Length != 3
                || !int.TryParse(parts[0], out var number)
                || !int.TryParse(parts[1], out var standardPoints)
                || !int.TryParse(parts[2], out var awesomePoints))
            {
                throw new FormatException($"Invalid assignment points on line {lineNumber}: '{line}'");
            }

            if (number < 0 || standardPoints < 0 || awesomePoints < 0)
            {
                throw new FormatException($"Negative number on line {lineNumber}: '{line}'");
            }

            if (!seenNumbers.Add(number))
            {
                throw new FormatException($"Duplicate assignment {number} on line {lineNumber}");
            }

            assignments.Add(new Assignment
            {
                Number = number,
                StandardPoints = standardPoints,
                AwesomePoints = awesomePoints
            });
        }

        return assignments;
    }

    private static string[] Split(string line)
    {
        var separator = line.Contains(';') && !line.Contains(',') ? ';' : ',';
        var parts = line.Split(separator).Select(p => p.Trim()).ToList();
        while (parts.Count > 1 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.ToArray();
    }
}
