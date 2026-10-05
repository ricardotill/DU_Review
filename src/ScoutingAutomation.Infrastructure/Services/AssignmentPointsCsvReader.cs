using System.Text;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Reads assignments from a CSV with the header "Opdracht,standaard,awesome[,titel[,beschrijving]]".
/// The assignment is a number or, for a special assignment, letters ("A"). Title and description are optional.
/// Commas or semicolons both work (Dutch Excel saves with semicolons): the first line decides which one.
/// A field may be quoted ("...", with "" for a quote) to hold the separator or a line break.
/// Empty fields at the end of a line are ignored.
/// </summary>
public static class AssignmentPointsCsvReader
{
    private const int MinColumns = 3;
    private const int MaxColumns = 5;

    public static IReadOnlyList<Assignment> Read(TextReader reader)
    {
        var assignments = new List<Assignment>();
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        char? separator = null;
        var isFirstRecord = true;
        var records = new RecordReader(reader);

        while (records.TryRead(out var record, out var lineNumber))
        {
            if (string.IsNullOrWhiteSpace(record))
            {
                continue;
            }

            separator ??= DetectSeparator(record);
            var parts = Split(record, separator.Value, lineNumber);

            // The first line is the header, unless it already holds an assignment: then nothing may be skipped
            if (isFirstRecord)
            {
                isFirstRecord = false;
                if (AssignmentCode.Normalize(parts[0]) == null || parts.Length < 2 || !int.TryParse(parts[1], out _))
                {
                    continue;
                }
            }

            if (parts.Length is < MinColumns or > MaxColumns
                || !int.TryParse(parts[1], out var standardPoints)
                || !int.TryParse(parts[2], out var awesomePoints))
            {
                throw new FormatException($"Invalid assignment points on line {lineNumber}: '{record}'");
            }

            var code = AssignmentCode.Normalize(parts[0])
                ?? throw new FormatException($"Invalid assignment '{parts[0]}' on line {lineNumber}: use a number or up to {AssignmentCode.MaxLetters} letters");

            if (standardPoints < 0 || awesomePoints < 0)
            {
                throw new FormatException($"Negative number on line {lineNumber}: '{record}'");
            }

            if (!seenCodes.Add(code))
            {
                throw new FormatException($"Duplicate assignment {code} on line {lineNumber}");
            }

            assignments.Add(new Assignment
            {
                Code = code,
                StandardPoints = standardPoints,
                AwesomePoints = awesomePoints,
                Title = Text(parts, 3, ScoutingDbContext.MaxAssignmentTitleLength, "title", lineNumber),
                Description = Text(parts, 4, ScoutingDbContext.MaxAssignmentDescriptionLength, "description", lineNumber)
            });
        }

        return assignments;
    }

    private static string? Text(string[] parts, int index, int maxLength, string what, int lineNumber)
    {
        if (parts.Length <= index || parts[index].Length == 0) return null;

        return parts[index].Length <= maxLength
            ? parts[index]
            : throw new FormatException($"The {what} on line {lineNumber} is longer than {maxLength} characters");
    }

    /// <summary>Reads records: one line each, or several when a quoted field holds a line break.</summary>
    private sealed class RecordReader(TextReader reader)
    {
        private int _linesRead;

        /// <param name="lineNumber">The line the record starts on.</param>
        public bool TryRead(out string record, out int lineNumber)
        {
            lineNumber = _linesRead + 1;
            var line = reader.ReadLine();
            if (line == null)
            {
                record = string.Empty;
                return false;
            }

            _linesRead++;
            var builder = new StringBuilder(line);
            while (HasOpenQuote(builder) && reader.ReadLine() is { } next)
            {
                _linesRead++;
                builder.Append('\n').Append(next);
            }

            record = builder.ToString();
            return true;
        }
    }

    private static bool HasOpenQuote(StringBuilder text)
    {
        var open = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') open = !open;
        }

        return open;
    }

    /// <summary>Semicolons when the first line has them outside quotes, otherwise commas.</summary>
    private static char DetectSeparator(string firstRecord)
    {
        var quoted = false;
        foreach (var c in firstRecord)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) return ';';
        }

        return ',';
    }

    private static string[] Split(string record, char separator, int lineNumber)
    {
        var parts = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < record.Length && record[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                parts.Add(field.ToString().Trim());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted)
        {
            throw new FormatException($"Unclosed quote on line {lineNumber}");
        }

        parts.Add(field.ToString().Trim());
        while (parts.Count > 1 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.ToArray();
    }
}
