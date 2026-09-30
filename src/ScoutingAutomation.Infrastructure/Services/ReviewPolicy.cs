using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// What each reaction means. Shared by live processing and the rebuild.
/// Who may review is kept by <see cref="RuntimeSettingsService"/>.
/// </summary>
public static class ReviewPolicy
{
    /// <summary>The review status an emoji stands for, or null when it has no meaning.</summary>
    public static SubmissionStatus? StatusFor(ScoutingSettings settings, string? reaction)
    {
        var name = reaction?.Trim(':').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return null;

        var config = settings.ReviewStatusReactions ?? new ReviewStatusReactionsConfig();
        if (Matches(config.Approved, name)) return SubmissionStatus.Approved;
        if (Matches(config.Rejected, name)) return SubmissionStatus.Rejected;
        if (Matches(config.Awesome, name)) return SubmissionStatus.Awesome;
        return null;
    }

    private static bool Matches(IEnumerable<string>? configured, string reaction) =>
        configured != null && configured.Any(c => c.Trim(':').Equals(reaction, StringComparison.OrdinalIgnoreCase));
}
