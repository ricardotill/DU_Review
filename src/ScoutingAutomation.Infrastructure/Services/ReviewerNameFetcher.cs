namespace ScoutingAutomation.Infrastructure.Services;

/// <param name="Updated">Names fetched and stored.</param>
/// <param name="KeptManual">Reviewers skipped because their name was entered by hand.</param>
/// <param name="Problems">Per reviewer, why the name could not be fetched.</param>
public sealed record ReviewerNameFetchResult(int Updated, int KeptManual, IReadOnlyList<string> Problems);

/// <summary>
/// Fills in reviewer names from their Slack profiles. Names entered by hand are never overwritten.
/// </summary>
public class ReviewerNameFetcher
{
    private readonly ISlackClient _slack;
    private readonly RuntimeSettingsService _runtime;

    public ReviewerNameFetcher(ISlackClient slack, RuntimeSettingsService runtime)
    {
        _slack = slack;
        _runtime = runtime;
    }

    /// <summary>False in simulation mode or without a bot token: then there is no Slack to ask.</summary>
    public bool CanFetch => _slack.IsConnected;

    /// <returns>Null when the name was stored or kept, otherwise why it could not be fetched.</returns>
    public async Task<string?> FetchAsync(string userId, CancellationToken ct = default)
    {
        if (_runtime.HasManualName(userId)) return null;

        var lookup = await _slack.GetUserNameAsync(userId, ct);
        if (lookup.Name != null)
        {
            _runtime.SetNameFromSlack(userId, lookup.Name);
            return null;
        }

        return lookup.Error switch
        {
            "user_not_found" => $"Slack kent {userId} niet. Controleer het member-ID.",
            "missing_scope" => "De Slack-app mist het recht users:read. Voeg het toe en installeer de app opnieuw.",
            _ => $"De naam van {userId} kon niet worden opgehaald: {lookup.Error}"
        };
    }

    public async Task<ReviewerNameFetchResult> FetchAllAsync(CancellationToken ct = default)
    {
        var updated = 0;
        var kept = 0;
        var problems = new List<string>();

        foreach (var reviewer in _runtime.AllReviewers)
        {
            if (_runtime.HasManualName(reviewer))
            {
                kept++;
                continue;
            }

            var problem = await FetchAsync(reviewer, ct);
            if (problem == null) updated++;
            else problems.Add(problem);
        }

        return new ReviewerNameFetchResult(updated, kept, problems);
    }
}
