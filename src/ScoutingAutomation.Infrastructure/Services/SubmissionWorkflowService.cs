using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;

namespace ScoutingAutomation.Infrastructure.Services;

public class SubmissionWorkflowService : ISubmissionWorkflowService
{
    private readonly ISubmissionRepository _submissionRepository;
    private readonly IGroupRepository _groupRepository;
    private readonly IAssignmentRepository _assignmentRepository;
    private readonly IAssignmentParser _assignmentParser;
    private readonly IFileStorageService _fileStorageService;
    private readonly ISlackClient _slackClient;
    private readonly ISubmissionChangeNotifier _changeNotifier;
    private readonly RuntimeSettingsService _runtime;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<SubmissionWorkflowService> _logger;

    public SubmissionWorkflowService(
        ISubmissionRepository submissionRepository,
        IGroupRepository groupRepository,
        IAssignmentRepository assignmentRepository,
        IAssignmentParser assignmentParser,
        IFileStorageService fileStorageService,
        ISlackClient slackClient,
        ISubmissionChangeNotifier changeNotifier,
        RuntimeSettingsService runtime,
        IOptions<ScoutingSettings> options,
        ILogger<SubmissionWorkflowService> logger)
    {
        _submissionRepository = submissionRepository;
        _groupRepository = groupRepository;
        _assignmentRepository = assignmentRepository;
        _assignmentParser = assignmentParser;
        _fileStorageService = fileStorageService;
        _slackClient = slackClient;
        _changeNotifier = changeNotifier;
        _runtime = runtime;
        _options = options;
        _logger = logger;
    }

    public Task<ProcessSlackMessageResult> ProcessSlackMessageAsync(SlackEventItemDto messageEvent, CancellationToken ct = default) =>
        ProcessMessageAsync(messageEvent, silent: false, manual: _runtime.ManualMode, ct);

    public Task<ProcessSlackMessageResult> ImportSlackMessageAsync(SlackEventItemDto message, CancellationToken ct = default) =>
        ProcessMessageAsync(message, silent: true, manual: false, ct);

    /// <param name="silent">True when importing history: Slack already saw this message, so no feedback is posted again.</param>
    /// <param name="manual">Manual mode: only download and name the files; no submissions, reviews or Slack feedback.</param>
    private async Task<ProcessSlackMessageResult> ProcessMessageAsync(SlackEventItemDto messageEvent, bool silent, bool manual, CancellationToken ct)
    {
        silent |= manual;

        var channelId = messageEvent.Channel;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            _logger.LogWarning("Incoming message missing channel ID");
            return ProcessSlackMessageResult.Ignored("Message missing channel ID");
        }

        // FR-001: Ignore unknown channels
        var group = await _groupRepository.GetBySlackChannelIdAsync(channelId, ct);
        if (group == null)
        {
            _logger.LogInformation("Ignored message from unknown channel {ChannelId}", channelId);
            return ProcessSlackMessageResult.Ignored($"Channel {channelId} is not mapped to any configured scouting group");
        }

        // Reviewers post in group channels too, but their messages are never submissions
        if (_runtime.IsReviewer(messageEvent.User))
        {
            _logger.LogDebug("Ignored message from reviewer {User} in {ChannelId}", messageEvent.User, channelId);
            return ProcessSlackMessageResult.Ignored($"User '{messageEvent.User}' is a reviewer");
        }

        // Check attachments
        var files = messageEvent.Files;
        if (files == null || files.Count == 0)
        {
            _logger.LogDebug("Message in {ChannelId} has no attached files, ignoring", channelId);
            return ProcessSlackMessageResult.Ignored("No files attached to message");
        }

        // FR-002: Parse assignment number
        // Without a clear assignment number nothing is stored: the scout is asked to post again
        var feedback = _options.Value.SlackFeedback ?? new SlackFeedbackConfig();
        var parseResult = _assignmentParser.Parse(messageEvent.Text);
        if (!parseResult.Success || !parseResult.AssignmentNumber.HasValue)
        {
            _logger.LogWarning("Skipped message '{Text}' in channel {Channel}: {Reason}",
                messageEvent.Text, channelId, parseResult.ErrorMessage);
            await SendFeedbackAsync(silent, feedback.MissingAssignmentNumber, channelId, messageEvent.Ts, null, null, ct);
            return ProcessSlackMessageResult.Ignored(parseResult.ErrorMessage ?? "No assignment number found");
        }

        var assignmentNumber = parseResult.AssignmentNumber.Value;
        if (await _assignmentRepository.GetByNumberAsync(assignmentNumber, ct) == null)
        {
            _logger.LogWarning("Skipped message in {Channel}: unknown assignment {Number}", channelId, assignmentNumber);
            await SendFeedbackAsync(silent, feedback.UnknownAssignmentNumber, channelId, messageEvent.Ts, "{number}", assignmentNumber.ToString(), ct);
            return ProcessSlackMessageResult.Ignored($"Assignment {assignmentNumber} does not exist");
        }

        var createdIds = new List<Guid>();
        var unsupportedFileNames = new List<string>();
        var failedFileNames = new List<string>();
        var storedFileNames = new List<string>();
        var messageTs = messageEvent.Ts ?? $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        // The message's own time, so rebuilt and live submissions show when they were really posted
        var postedAt = SlackTimestamp.ToDateTimeOffset(messageEvent.Ts) ?? DateTimeOffset.UtcNow;
        // Fetched once per message, and only when a submission is actually created
        string? submitterName = null;
        var submitterNameFetched = false;

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Name)) continue;

            if (!IsSupportedFile(file))
            {
                _logger.LogInformation("Skipping unsupported file {FileName} ({Mimetype})", file.Name, file.Mimetype);
                unsupportedFileNames.Add(file.Name);
                continue;
            }

            var exists = manual
                ? !_runtime.TryClaimManualDownload(channelId, messageTs, file.Name)
                : await _submissionRepository.ExistsAsync(channelId, messageTs, file.Name, ct);
            if (exists)
            {
                _logger.LogInformation("Submission already registered for file {FileName} in message {Ts}", file.Name, messageTs);
                continue;
            }

            // FR-004: Download eligible file
            var downloadUrl = file.UrlPrivateDownload ?? file.UrlPrivate ?? $"sim://{file.Name}";
            Stream stream;
            try
            {
                stream = await _slackClient.DownloadFileAsync(downloadUrl, file.Name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not download {FileName} from message {Ts} in {Channel}", file.Name, messageTs, channelId);
                failedFileNames.Add(file.Name);
                continue;
            }

            await using var _ = stream;

            // FR-005: Save to /group-name/ until reviewed
            var storedFileName = SubmissionFileName.Build(group, assignmentNumber, file.Name);
            var storagePath = await _fileStorageService.SaveInitialFileAsync(group.Name, storedFileName, stream, ct);
            storedFileNames.Add(Path.GetFileName(storagePath));

            if (manual)
            {
                _logger.LogInformation("Manual mode: saved {File} for group {Group}", storagePath, group.Name);
                continue;
            }

            if (!submitterNameFetched)
            {
                submitterName = await FetchSubmitterNameAsync(messageEvent.User, ct);
                submitterNameFetched = true;
            }

            // FR-006: Create submission record
            var submission = new Submission
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                SlackChannelId = channelId,
                SlackMessageId = messageTs,
                Submitter = messageEvent.User ?? "Unknown",
                SubmitterName = submitterName,
                FileName = file.Name,
                FileSizeBytes = file.Size > 0 ? file.Size : new FileInfo(storagePath).Length,
                ContentType = file.Mimetype ?? "application/octet-stream",
                MessageText = messageEvent.Text,
                CreatedAt = postedAt
            };

            submission.MarkPending(storagePath, assignmentNumber);

            await _submissionRepository.AddAsync(submission, ct);
            createdIds.Add(submission.Id);

            _logger.LogInformation("Registered submission {Id} for group {Group}, assignment #{Assignment}, file {File}",
                submission.Id, group.Name, assignmentNumber, file.Name);
        }

        if (createdIds.Count > 0)
        {
            _changeNotifier.NotifyChanged();
        }

        if (unsupportedFileNames.Count > 0)
        {
            await SendFeedbackAsync(silent, feedback.UnsupportedFile, channelId, messageEvent.Ts, "{files}", string.Join(", ", unsupportedFileNames), ct);
        }

        if (failedFileNames.Count > 0)
        {
            await SendFeedbackAsync(silent, feedback.DownloadFailed, channelId, messageEvent.Ts, "{files}", string.Join(", ", failedFileNames), ct);
        }

        var summary = manual
            ? $"Manual mode: saved {storedFileNames.Count} file(s)"
            : $"Successfully registered {createdIds.Count} submission(s)";
        return ProcessSlackMessageResult.Succeeded(createdIds, summary) with
        {
            UnsupportedFiles = unsupportedFileNames,
            FailedDownloads = failedFileNames,
            StoredFiles = storedFileNames
        };
    }

    public async Task<ProcessSlackReactionResult> ProcessSlackReactionAsync(SlackEventItemDto reactionEvent, CancellationToken ct = default)
    {
        var reaction = reactionEvent.Reaction?.Trim(':').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(reaction))
        {
            return ProcessSlackReactionResult.Ignored("Empty reaction emoji");
        }

        if (_runtime.ManualMode)
        {
            return ProcessSlackReactionResult.Ignored("Manual mode: reactions are not processed");
        }

        // Only reactions from configured reviewers may change a submission's status
        var reviewer = reactionEvent.User;
        if (!_runtime.IsReviewer(reviewer))
        {
            _logger.LogInformation("Ignored reaction '{Reaction}' from non-reviewer {User}", reaction, reviewer);
            return ProcessSlackReactionResult.Ignored($"User '{reviewer}' is not a configured reviewer");
        }

        var targetChannel = reactionEvent.Item?.Channel;
        var targetTs = reactionEvent.Item?.Ts;

        if (string.IsNullOrWhiteSpace(targetChannel) || string.IsNullOrWhiteSpace(targetTs))
        {
            return ProcessSlackReactionResult.Failed("Reaction event missing target channel or timestamp");
        }

        // FR-007: Review status reactions mapping
        var targetStatus = ReviewPolicy.StatusFor(_options.Value, reaction);
        if (!targetStatus.HasValue)
        {
            _logger.LogDebug("Reaction '{Reaction}' is not mapped to any review status", reaction);
            return ProcessSlackReactionResult.Ignored($"Reaction '{reaction}' has no review mapping configured");
        }

        // A message can carry several files: the reaction reviews all of them
        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(targetChannel, targetTs, ct);
        if (submissions.Count == 0)
        {
            _logger.LogWarning("No submission found matching Slack channel {Channel} and message ts {Ts}", targetChannel, targetTs);
            return ProcessSlackReactionResult.Failed($"Submission not found for channel {targetChannel}, ts {targetTs}");
        }

        await ApplyStatusAsync(submissions, targetStatus.Value, reviewer!, reaction, ct);

        _logger.LogInformation("Processed reaction '{Reaction}' on message {Ts}: {Count} submission(s) now {Status}",
            reaction, targetTs, submissions.Count, targetStatus.Value);

        return ProcessSlackReactionResult.Succeeded(submissions[0].Id, targetStatus.Value);
    }

    public async Task<ImportedReviewResult> ApplyImportedReactionsAsync(string channelId, string messageTs, IReadOnlyList<SlackMessageReactionDto>? reactions, CancellationToken ct = default)
    {
        var review = ChooseReview(reactions);
        if (review == null)
        {
            return new ImportedReviewResult(null, Conflict: false);
        }

        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(channelId, messageTs, ct);
        if (submissions.Count > 0)
        {
            await ApplyStatusAsync(submissions, review.Status, review.Reviewer, review.Emoji, ct);
        }

        return new ImportedReviewResult(review.Status, review.Conflict);
    }

    public async Task<ProcessSlackReactionResult> ProcessSlackReactionRemovedAsync(SlackEventItemDto reactionEvent, CancellationToken ct = default)
    {
        var reaction = reactionEvent.Reaction?.Trim(':').ToLowerInvariant();
        var channel = reactionEvent.Item?.Channel;
        var messageTs = reactionEvent.Item?.Ts;

        if (_runtime.ManualMode)
        {
            return ProcessSlackReactionResult.Ignored("Manual mode: reactions are not processed");
        }

        // Only a reviewer taking back a review emoji can change a status
        if (!_runtime.IsReviewer(reactionEvent.User) || ReviewPolicy.StatusFor(_options.Value, reaction) == null)
        {
            return ProcessSlackReactionResult.Ignored($"Removing '{reaction}' by '{reactionEvent.User}' does not affect reviews");
        }

        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(messageTs))
        {
            return ProcessSlackReactionResult.Failed("Reaction event missing target channel or timestamp");
        }

        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(channel, messageTs, ct);
        if (submissions.Count == 0)
        {
            return ProcessSlackReactionResult.Ignored($"No submission for channel {channel}, ts {messageTs}");
        }

        // Other reviewers may still have reacted: what counts is what is on the message now
        var remaining = await _slackClient.GetReactionsAsync(channel, messageTs, ct);
        if (remaining == null)
        {
            _logger.LogWarning("Could not read reactions on message {Ts} in {Channel}; status left unchanged", messageTs, channel);
            return ProcessSlackReactionResult.Failed("Current reactions could not be read from Slack; status unchanged");
        }

        var review = ChooseReview(remaining);
        var newStatus = review?.Status ?? SubmissionStatus.Pending;
        if (submissions.All(s => s.Status == newStatus))
        {
            return ProcessSlackReactionResult.Ignored($"Status stays {newStatus}");
        }

        if (review == null)
        {
            await ApplyStatusAsync(submissions, SubmissionStatus.Pending, reactionEvent.User!, reaction, ct, notes: "Review emoji removed");
        }
        else
        {
            await ApplyStatusAsync(submissions, review.Status, review.Reviewer, review.Emoji, ct);
        }

        _logger.LogInformation("Reaction '{Reaction}' removed from message {Ts}: {Count} submission(s) now {Status}",
            reaction, messageTs, submissions.Count, newStatus);

        return ProcessSlackReactionResult.Succeeded(submissions[0].Id, newStatus);
    }

    private sealed record ChosenReview(SubmissionStatus Status, string Reviewer, string Emoji, bool Conflict);

    /// <summary>
    /// The review that counts among the reactions on a message. Slack does not say in which order reactions
    /// were placed, so when reviewers disagree the best status wins. Null when no reviewer placed a review emoji.
    /// </summary>
    private ChosenReview? ChooseReview(IReadOnlyList<SlackMessageReactionDto>? reactions)
    {
        // Every review emoji placed by a reviewer, in the order Slack lists them
        var reviews = (reactions ?? Array.Empty<SlackMessageReactionDto>())
            .SelectMany(r => (r.Users ?? new List<string>())
                .Where(user => _runtime.IsReviewer(user))
                .Select(user => (Reviewer: user, Emoji: r.Name!.Trim(':').ToLowerInvariant(), Status: ReviewPolicy.StatusFor(_options.Value, r.Name))))
            .Where(r => r.Status.HasValue)
            .ToList();

        if (reviews.Count == 0)
        {
            return null;
        }

        var best = reviews.Select(r => r.Status!.Value).Best();
        var winner = reviews.First(r => r.Status == best);
        return new ChosenReview(best, winner.Reviewer, winner.Emoji, Conflict: reviews.Select(r => r.Status).Distinct().Count() > 1);
    }

    private async Task ApplyStatusAsync(IReadOnlyList<Submission> submissions, SubmissionStatus status, string reviewer, string? emoji, CancellationToken ct, string? notes = null)
    {
        foreach (var submission in submissions)
        {
            var group = submission.Group ?? await _groupRepository.GetByIdAsync(submission.GroupId, ct);
            var groupName = group?.Name ?? "unknown-group";

            // FR-008: Move file
            var newStoragePath = await _fileStorageService.MoveFileToStatusAsync(groupName, submission.StoragePath, status, ct);
            submission.TransitionStatus(status, newStoragePath, reviewer, emoji, notes);
            await _submissionRepository.UpdateAsync(submission, ct);
        }

        _changeNotifier.NotifyChanged();
    }

    public const string DashboardOverrideReviewer = "Dashboard override";

    public async Task<Submission> OverrideStatusAsync(Guid submissionId, SubmissionStatus newStatus, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for a status override", nameof(reason));
        }

        var submission = await _submissionRepository.GetByIdAsync(submissionId, ct)
            ?? throw new InvalidOperationException($"Submission {submissionId} not found");

        await ApplyStatusAsync(new[] { submission }, newStatus, DashboardOverrideReviewer, emoji: null, ct, notes: reason.Trim());

        _logger.LogWarning("Submission {Id} status overridden from the dashboard to {Status}. Reason: {Reason}", submissionId, newStatus, reason);
        return submission;
    }

    /// <returns>The Slack name of the user, or null when there is no user or the name could not be fetched.</returns>
    private async Task<string?> FetchSubmitterNameAsync(string? userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId) || !_slackClient.IsConnected) return null;

        var lookup = await _slackClient.GetUserNameAsync(userId, ct);
        if (lookup?.Name == null)
        {
            _logger.LogWarning("Could not fetch the name of submitter {User}: {Error}", userId, lookup?.Error);
            return null;
        }

        return lookup.Name.Length > 100 ? lookup.Name[..100] : lookup.Name;
    }

    private async Task SendFeedbackAsync(bool silent, FeedbackConfig? config, string channel, string? messageTs, string? placeholder, string? value, CancellationToken ct)
    {
        if (silent || config == null || string.IsNullOrWhiteSpace(messageTs)) return;

        // A reaction that was already there means this message got feedback before
        if (!string.IsNullOrWhiteSpace(config.Reaction)
            && !await _slackClient.AddReactionAsync(channel, messageTs, config.Reaction, ct))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(config.Message))
        {
            var text = placeholder != null ? config.Message.Replace(placeholder, value) : config.Message;
            await _slackClient.PostThreadReplyAsync(channel, messageTs, text, ct);
        }
    }

    private bool IsSupportedFile(SlackFileItemDto file)
    {
        var ext = Path.GetExtension(file.Name);
        var extensions = _options.Value.SupportedExtensions ?? new List<string>();
        if (!string.IsNullOrEmpty(ext) && extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // SVG can carry scripts, so it is never accepted by MIME type alone
        var mimetype = file.Mimetype;
        if (string.IsNullOrWhiteSpace(mimetype) || mimetype.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefixes = _options.Value.SupportedMimeTypePrefixes ?? new List<string>();
        return prefixes.Any(p => mimetype.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
