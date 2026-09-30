using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Reads assignment points from a CSV with the header "Opdracht,standaard,awesome".
/// </summary>
public static class AssignmentPointsCsvReader
{
    public static IReadOnlyList<Assignment> Read(TextReader reader)
    {
        var assignments = new List<Assignment>();
        var seenNumbers = new HashSet<int>();
        var lineNumber = 0;

        // Skip header
        if (reader.ReadLine() == null)
        {
            return assignments;
        }
        lineNumber++;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length != 3
                || !int.TryParse(parts[0].Trim(), out var number)
                || !int.TryParse(parts[1].Trim(), out var standardPoints)
                || !int.TryParse(parts[2].Trim(), out var awesomePoints))
            {
                throw new FormatException($"Invalid assignment points on line {lineNumber}: '{line}'");
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
}
