using System.Text.RegularExpressions;
using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <param name="Kind">Check-in or theme; null when the text names neither.</param>
/// <param name="Number">The location or theme number, without leading zeros.</param>
public sealed record AdventurersParseResult(SubmissionKind? Kind, string? Number)
{
    public bool Success => Kind != null && Number != null;

    public static AdventurersParseResult Failed { get; } = new(null, null);
}

/// <summary>
/// Reads what an Avonturiers message is for: "Locatie 7", "Loc 7", "L7" or a bare "#7" is a check-in;
/// a half-hour assignment (a theme) is "Opdracht 3", "Half-uur opdracht 3", "Halfuuropdracht 3", "Opdr 3", "Thema 3",
/// "T3" or "O3": in the Avonturiers channel the usual assignment wording means the half-hour assignment.
/// Spelled-out words win over short forms, which win over "#7"; within a tier the first one in the text counts.
/// </summary>
public static partial class AdventurersParser
{
    /// <summary>Highest location or theme number; anything longer is not a number a team writes.</summary>
    public const int MaxNumber = 9999;

    [GeneratedRegex(@"\b(?:locatie|location|loc)\s*[:#.-]?\s*(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LocationWord();

    [GeneratedRegex(@"\b(?:half[\s-]*uur[\s-]*opdracht|thema|theme|opdracht|opdr)\s*[:#.-]?\s*(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeWord();

    [GeneratedRegex(@"\b[lL]\s?(\d{1,5})\b")]
    private static partial Regex LocationShort();

    [GeneratedRegex(@"\b[tToO]\s?(\d{1,5})\b")]
    private static partial Regex ThemeShort();

    [GeneratedRegex(@"(?:^|\s)#\s*(\d{1,5})\b")]
    private static partial Regex Hash();

    public static AdventurersParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AdventurersParseResult.Failed;

        var tiers = new[]
        {
            new[] { (LocationWord(), SubmissionKind.CheckIn), (ThemeWord(), SubmissionKind.Theme) },
            new[] { (LocationShort(), SubmissionKind.CheckIn), (ThemeShort(), SubmissionKind.Theme) },
            new[] { (Hash(), SubmissionKind.CheckIn) }
        };

        foreach (var tier in tiers)
        {
            var first = tier
                .Select(p => (Match: p.Item1.Match(text), Kind: p.Item2))
                .Where(m => m.Match.Success)
                .OrderBy(m => m.Match.Index)
                .FirstOrDefault();
            if (first.Match == null) continue;

            // "Locatie 0" or a five-digit number is no location: the team is asked for the number again
            return int.TryParse(first.Match.Groups[1].Value, out var number) && number is > 0 and <= MaxNumber
                ? new AdventurersParseResult(first.Kind, number.ToString())
                : AdventurersParseResult.Failed;
        }

        return AdventurersParseResult.Failed;
    }
}
