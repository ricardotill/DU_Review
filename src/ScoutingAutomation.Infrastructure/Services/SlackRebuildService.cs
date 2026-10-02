using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Infrastructure.Services;

public enum RebuildState
{
    Idle,
    Running,
    Completed,
    Failed
}

/// <summary>A group channel that could not be read from Slack, and what to do about it.</summary>
public sealed record RebuildChannelProblem(string Group, string ChannelId, string Error, string Advice);

/// <summary>A message that was not imported, or whose review needs attention.</summary>
public sealed record RebuildIssue(string Group, DateTimeOffset PostedAt, string Reason, string? Text);

/// <summary>
/// Progress and outcome of the latest rebuild. Replaced as a whole on every change, so readers never see a half update.
/// </summary>
public sealed record RebuildProgress
{
    public RebuildState State { get; init; }
    public DateTimeOffset? Since { get; init; }

    /// <summary>The one group being rebuilt; null when all groups are.</summary>
    public string? GroupName { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string Step { get; init; } = "";
    public int ChannelsRead { get; init; }
    public int ChannelsTotal { get; init; }
    public int MessagesFound { get; init; }
    public int MessagesProcessed { get; init; }
    public int SubmissionsImported { get; init; }
    public int ReviewsApplied { get; init; }
    public IReadOnlyList<RebuildIssue> Skipped { get; init; } = Array.Empty<RebuildIssue>();
    public IReadOnlyList<RebuildIssue> Conflicts { get; init; } = Array.Empty<RebuildIssue>();
    public IReadOnlyList<RebuildChannelProblem> ChannelProblems { get; init; } = Array.Empty<RebuildChannelProblem>();
    public string? BackupFolder { get; init; }
    public string? Error { get; init; }
}

public interface ISlackRebuildService
{
    RebuildProgress Progress { get; }

    /// <summary>Raised on every progress change, from a background thread.</summary>
    event Action? Changed;

    /// <summary>Whether a rebuild is possible with the current settings (live mode with a bot token).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Starts a rebuild in the background: of one group, or of all groups when <paramref name="groupId"/> is null.
    /// Returns false when one is already running.
    /// </summary>
    bool TryStart(DateTimeOffset since, Guid? groupId = null);

    /// <summary>The running or last rebuild, for waiting on it.</summary>
    Task Completion { get; }
}

/// <summary>
/// Rebuilds the submissions of all groups, or of one group, from the Slack messages since a given time:
/// backs up the current data, clears it, and processes every message and reaction again.
/// </summary>
public class SlackRebuildService : ISlackRebuildService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly SlackProcessingGate _gate;
    private readonly IFileStorageService _storage;
    private readonly ISubmissionChangeNotifier _changeNotifier;
    private readonly RuntimeSettingsService _runtime;
    private readonly ILogger<SlackRebuildService> _logger;
    private readonly object _lock = new();

    public SlackRebuildService(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        SlackProcessingGate gate,
        IFileStorageService storage,
        ISubmissionChangeNotifier changeNotifier,
        RuntimeSettingsService runtime,
        ILogger<SlackRebuildService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _gate = gate;
        _storage = storage;
        _changeNotifier = changeNotifier;
        _runtime = runtime;
        _logger = logger;
    }

    public RebuildProgress Progress { get; private set; } = new();

    public event Action? Changed;

    public Task Completion { get; private set; } = Task.CompletedTask;

    public bool IsAvailable =>
        !_options.Value.Slack.SimulationMode && !string.IsNullOrWhiteSpace(_options.Value.Slack.BotToken);

    public bool TryStart(DateTimeOffset since, Guid? groupId = null)
    {
        // Slack is not read before the event started: those messages never count
        since = _runtime.NotBeforeEventStart(since);
        lock (_lock)
        {
            if (Progress.State == RebuildState.Running)
            {
                return false;
            }

            Update(_ => new RebuildProgress { State = RebuildState.Running, Since = since, StartedAt = DateTimeOffset.UtcNow, Step = "Starten" });
            Completion = Task.Run(() => RunAsync(since, groupId));
            return true;
        }
    }

    private async Task RunAsync(DateTimeOffset since, Guid? groupId)
    {
        try
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException("Opnieuw opbouwen kan alleen met Slack verbonden (SimulationMode uit) en met een bot-token");
            }

            using (await _gate.CloseAsync())
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await RebuildAsync(scope.ServiceProvider, since, groupId);
            }

            Update(p => p with { State = RebuildState.Completed, Step = "Klaar", FinishedAt = DateTimeOffset.UtcNow });
            _logger.LogInformation("Rebuild from Slack finished: {Imported} submissions, {Skipped} skipped", Progress.SubmissionsImported, Progress.Skipped.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rebuild from Slack failed");
            Update(p => p with { State = RebuildState.Failed, Error = ex.Message, FinishedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            _changeNotifier.NotifyChanged();
        }
    }

    private async Task RebuildAsync(IServiceProvider services, DateTimeOffset since, Guid? groupId)
    {
        var groupRepository = services.GetRequiredService<IGroupRepository>();
        IReadOnlyList<Group> groups;
        Group? onlyGroup = null;
        if (groupId.HasValue)
        {
            onlyGroup = await groupRepository.GetByIdAsync(groupId.Value)
                ?? throw new InvalidOperationException("De groep om opnieuw op te bouwen bestaat niet");
            groups = new[] { onlyGroup };
            Update(p => p with { GroupName = onlyGroup.Name });
        }
        else
        {
            groups = await groupRepository.GetAllAsync();
        }

        var submissions = services.GetRequiredService<ISubmissionRepository>();
        var workflow = services.GetRequiredService<ISubmissionWorkflowService>();
        var history = services.GetRequiredService<ISlackHistoryClient>();
        var links = services.GetService<SlackLinks>();
        if (links != null) await links.EnsureLoadedAsync();
        var botUserId = links?.BotUserId;

        // 1. Read every channel first: if any fails, nothing has been touched yet
        Update(p => p with { Step = "Slack lezen", ChannelsTotal = groups.Count });
        var messages = new List<(Group Group, SlackEventItemDto Message)>();
        var problems = new List<RebuildChannelProblem>();
        foreach (var group in groups)
        {
            try
            {
                var channelMessages = await history.GetChannelMessagesAsync(group.SlackChannelId, since);
                messages.AddRange(channelMessages.Select(m => (group, m)));
                Update(p => p with { ChannelsRead = p.ChannelsRead + 1, MessagesFound = messages.Count });
            }
            catch (Exception ex) when (ex is SlackApiException or HttpRequestException)
            {
                var error = ex is SlackApiException slack ? slack.Error : ex.Message;
                _logger.LogError(ex, "Could not read Slack channel {Channel} of group {Group}: {Error}", group.SlackChannelId, group.Name, error);
                problems.Add(new RebuildChannelProblem(group.Name, group.SlackChannelId, error, AdviceFor(error)));
            }
        }

        if (problems.Count > 0)
        {
            Update(p => p with { ChannelProblems = problems });
            throw new InvalidOperationException($"{problems.Count} kanaal/kanalen konden niet worden gelezen; er is niets veranderd");
        }

        // 2. Back up and clear the current data
        Update(p => p with { Step = "Back-up maken" });
        var existing = await submissions.SearchSubmissionsAsync(new SubmissionFilter { GroupId = onlyGroup?.Id });
        string backup;
        if (onlyGroup != null)
        {
            backup = await _storage.MoveGroupToBackupAsync(onlyGroup.Name);
            await WriteBackupAsync(backup, existing);
            await submissions.DeleteByGroupAsync(onlyGroup.Id);
        }
        else
        {
            backup = await _storage.MoveAllToBackupAsync();
            await WriteBackupAsync(backup, existing);
            await submissions.DeleteAllAsync();
        }

        foreach (var group in groups)
        {
            await _storage.EnsureFolderStructureAsync(group.Name);
        }

        Update(p => p with { BackupFolder = backup, Step = "Importeren" });

        // 3. Import in the order the messages were posted, so resubmissions are numbered as they were live
        var ordered = messages.OrderBy(m => SlackTimestamp.ToDateTimeOffset(m.Message.Ts)).ToList();
        var imported = new List<(Group Group, SlackEventItemDto Message)>();
        foreach (var (group, message) in ordered)
        {
            if (IsCandidate(message, _runtime, botUserId))
            {
                var result = await workflow.ImportSlackMessageAsync(message);
                if (result.CreatedSubmissionIds.Count > 0) imported.Add((group, message));

                foreach (var issue in Issues(group, message, result))
                {
                    Update(p => p with { Skipped = p.Skipped.Append(issue).ToList() });
                }

                Update(p => p with { SubmissionsImported = p.SubmissionsImported + result.CreatedSubmissionIds.Count });
            }

            Update(p => p with { MessagesProcessed = p.MessagesProcessed + 1 });
        }

        // 4. Apply the reviews as they stand in Slack now
        Update(p => p with { Step = "Beoordelingen verwerken" });
        foreach (var (group, message) in imported)
        {
            var review = await workflow.ApplyImportedReactionsAsync(message.Channel!, message.Ts!, message.Reactions);
            if (review.Status.HasValue)
            {
                Update(p => p with { ReviewsApplied = p.ReviewsApplied + 1 });
            }

            if (review.Conflict)
            {
                var issue = Issue(group, message, $"Juryleden zijn het oneens; {review.Status?.StatusLabel()} toegepast");
                Update(p => p with { Conflicts = p.Conflicts.Append(issue).ToList() });
            }
        }
    }

    private static string AdviceFor(string error) => error switch
    {
        "not_in_channel" => "Nodig de bot uit in dit kanaal: typ daar /invite @DU Review.",
        "channel_not_found" => "Controleer dit kanaal-ID onder Groups in appsettings.json. Bij een privékanaal moet de bot lid zijn.",
        "missing_scope" => "De Slack-app mist channels:history of groups:history. Voeg de rechten toe en installeer de app opnieuw.",
        "invalid_auth" or "not_authed" or "token_revoked" or "account_inactive" => "Het bot-token is niet geldig. Kopieer het Bot User OAuth Token opnieuw.",
        _ => "Bekijk het app-log voor details en controleer het kanaal in Slack."
    };

    /// <summary>Messages from Slack's history that could be submissions: from people, with files, not from reviewers.</summary>
    /// <param name="botUserId">The bot's own user ID, when known: a picture the bot shared need not carry bot_id.</param>
    public static bool IsCandidate(SlackEventItemDto message, RuntimeSettingsService runtime, string? botUserId = null) =>
        message.BotId == null
        && (botUserId == null || message.User != botUserId)
        && message.Subtype is null or "file_share" or "thread_broadcast"
        && message.Files is { Count: > 0 }
        && !runtime.IsReviewer(message.User)
        && !runtime.IsBeforeEventStart(message.Ts)
        && !runtime.IsAfterEventEnd(message.Ts);

    private static IEnumerable<RebuildIssue> Issues(Group group, SlackEventItemDto message, ProcessSlackMessageResult result)
    {
        if (!result.Success)
        {
            yield return Issue(group, message, result.Message);
        }

        if (result.UnsupportedFiles.Count > 0)
        {
            yield return Issue(group, message, $"Bestandstype niet ondersteund: {string.Join(", ", result.UnsupportedFiles)}");
        }

        if (result.FailedDownloads.Count > 0)
        {
            yield return Issue(group, message, $"Download mislukt: {string.Join(", ", result.FailedDownloads)}");
        }
    }

    private static RebuildIssue Issue(Group group, SlackEventItemDto message, string reason) =>
        new(group.Name, SlackTimestamp.ToDateTimeOffset(message.Ts) ?? DateTimeOffset.MinValue, reason, message.Text);

    private static async Task WriteBackupAsync(string folder, IReadOnlyList<Submission> submissions)
    {
        // Plain copies: the entities reference each other, which JSON cannot represent
        var export = submissions.Select(s => new
        {
            s.Id, Group = s.Group?.Name, s.GroupId, s.AssignmentNumber, Status = s.Status.ToString(), s.FileName, s.StoragePath,
            s.ContentType, s.FileSizeBytes, s.Submitter, s.SubmitterName, s.SlackChannelId, s.SlackMessageId, s.MessageText, s.UnresolvedReason,
            s.CreatedAt, s.UpdatedAt,
            Reviews = s.Reviews.Select(r => new { r.Reviewer, Status = r.Status.ToString(), r.ReactionEmoji, r.Notes, r.CreatedAt })
        });

        await using var file = File.Create(Path.Combine(folder, "submissions.json"));
        await JsonSerializer.SerializeAsync(file, export, new JsonSerializerOptions { WriteIndented = true });
    }

    private void Update(Func<RebuildProgress, RebuildProgress> change)
    {
        lock (_lock)
        {
            Progress = change(Progress);
        }

        Changed?.Invoke();
    }
}
