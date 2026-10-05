namespace ScoutingAutomation.Domain.Models;

/// <summary>
/// An assignment is known by its code: a number ("12") or, for special assignments, letters ("A").
/// Codes are stored normalised, so "012" and "12", or "a" and "A", are the same assignment.
/// </summary>
public static class AssignmentCode
{
    /// <summary>The longest code; the database column holds this many characters.</summary>
    public const int MaxLength = 10;

    /// <summary>The most letters a letter code may have, e.g. "AB".</summary>
    public const int MaxLetters = 3;

    /// <summary>
    /// The code as stored: a number without leading zeros, or letters in upper case.
    /// Null for anything else, including 0 and an empty string.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var code = raw?.Trim();
        if (string.IsNullOrEmpty(code)) return null;

        if (code.All(char.IsAsciiDigit))
        {
            var digits = code.TrimStart('0');
            return digits.Length is > 0 and <= 6 ? digits : null;
        }

        return code.Length <= MaxLetters && code.All(char.IsAsciiLetter) ? code.ToUpperInvariant() : null;
    }

    /// <summary>Whether this (normalised) code is a special assignment, given with letters.</summary>
    public static bool IsLetter(string code) => code.Length > 0 && char.IsAsciiLetter(code[0]);

    /// <summary>Numbers first, by value (2 before 10); then letter codes, A to Z.</summary>
    public static IComparer<string?> Comparer { get; } = Comparer<string?>.Create(Compare);

    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        var aLetter = IsLetter(a);
        var bLetter = IsLetter(b);
        if (aLetter != bLetter) return aLetter ? 1 : -1;
        if (aLetter) return string.CompareOrdinal(a, b);

        // Both numbers: the shorter one is smaller, as codes have no leading zeros
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }
}
