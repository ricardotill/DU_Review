using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Infrastructure.Workflow;

public partial class AssignmentParser : IAssignmentParser
{
    private readonly ILogger<AssignmentParser> _logger;

    // Matches patterns like "#12", "# 12"
    [GeneratedRegex(@"(?:^|\s)#\s*(\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex HashNumberRegex();

    // Matches "assignment 12", "opdracht 12", "taak 12", "task: 12", "challenge - 12"
    [GeneratedRegex(@"\b(?:opdracht|assignment|taak|task|challenge)\s*[:#-]?\s*(\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeywordNumberRegex();

    // Matches "A12", "a 12"
    [GeneratedRegex(@"\b[aA]\s*(\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LetterPrefixRegex();

    // Matches standalone number at start of string or followed by separator: "12 - campfire" or "12: campfire"
    [GeneratedRegex(@"^\s*(\d{1,4})\s*[-:.)]", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumberRegex();

    // Special assignments by letter, only after a keyword or a hash: "opdracht A", "Opdracht: b", "#C".
    // Plain text never counts: "a photo" or "taak a 12" (that is assignment 12) must not become assignment A.
    [GeneratedRegex(@"\b(?:opdracht|assignment|taak|task|challenge)\s*[:#-]?\s*([a-z]{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeywordLetterRegex();

    [GeneratedRegex(@"(?:^|\s)#\s*([a-z]{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex HashLetterRegex();

    public AssignmentParser(ILogger<AssignmentParser> logger)
    {
        _logger = logger;
    }

    public AssignmentParseResult Parse(string? messageText)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            _logger.LogWarning("Assignment parsing failed: message text is empty");
            return AssignmentParseResult.Failed("Tekst van het bericht is leeg");
        }

        // 1. Try keyword regex: "opdracht 5", "assignment 12"
        var keywordMatch = KeywordNumberRegex().Match(messageText);
        if (keywordMatch.Success && int.TryParse(keywordMatch.Groups[1].Value, out var keywordNum) && keywordNum > 0)
        {
            _logger.LogInformation("Parsed assignment {Number} via keyword pattern from '{Text}'", keywordNum, messageText);
            return AssignmentParseResult.Succeeded(keywordNum.ToString());
        }

        // 2. Try hash regex: "#5", "# 12"
        var hashMatch = HashNumberRegex().Match(messageText);
        if (hashMatch.Success && int.TryParse(hashMatch.Groups[1].Value, out var hashNum) && hashNum > 0)
        {
            _logger.LogInformation("Parsed assignment {Number} via hash pattern from '{Text}'", hashNum, messageText);
            return AssignmentParseResult.Succeeded(hashNum.ToString());
        }

        // 3. Try letter prefix regex: "A5", "a12"
        var letterMatch = LetterPrefixRegex().Match(messageText);
        if (letterMatch.Success && int.TryParse(letterMatch.Groups[1].Value, out var letterNum) && letterNum > 0)
        {
            _logger.LogInformation("Parsed assignment {Number} via letter prefix from '{Text}'", letterNum, messageText);
            return AssignmentParseResult.Succeeded(letterNum.ToString());
        }

        // 4. Try leading number pattern: "12 - photo of camp"
        var leadingMatch = LeadingNumberRegex().Match(messageText);
        if (leadingMatch.Success && int.TryParse(leadingMatch.Groups[1].Value, out var leadingNum) && leadingNum > 0)
        {
            _logger.LogInformation("Parsed assignment {Number} via leading number from '{Text}'", leadingNum, messageText);
            return AssignmentParseResult.Succeeded(leadingNum.ToString());
        }

        // 5. Letters last, so any number in the text wins: "opdracht A", "#B"
        foreach (var match in new[] { KeywordLetterRegex().Match(messageText), HashLetterRegex().Match(messageText) })
        {
            if (match.Success && AssignmentCode.Normalize(match.Groups[1].Value) is { } letters)
            {
                _logger.LogInformation("Parsed special assignment {Code} from '{Text}'", letters, messageText);
                return AssignmentParseResult.Succeeded(letters);
            }
        }

        _logger.LogWarning("Could not identify assignment number from message: '{Text}'", messageText);
        return AssignmentParseResult.Failed($"Geen geldig opdrachtnummer gevonden in: '{messageText}'");
    }
}
