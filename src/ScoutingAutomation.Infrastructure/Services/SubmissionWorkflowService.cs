using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Exceptions;
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

        var supportedFiles = SupportedFiles(files);

        foreach (var file in files)
        {
            // A deleted file still shows in the message as a "tombstone", without content
            if (string.IsNullOrWhiteSpace(file.Name) || file.IsDeleted) continue;

            if (!supportedFiles.Contains(file))
            {
                _logger.LogInformation("Skipping unsupported file {FileName} ({Mimetype})", file.Name, file.Mimetype);
                unsupportedFileNames.Add(file.Name);
                continue;
            }

            var exists = manual
                ? !_runtime.TryClaimManualDownload(channelId, messageTs, file.Id ?? file.Name)
                : await _submissionRepository.ExistsAsync(channelId, messageTs, file.Id, file.Name, ct);
            if (exists)
            {
                _logger.LogInformation("Submission already registered for file {FileName} in message {Ts}", file.Name, messageTs);
                continue;
            }

            // FR-004 / FR-005: Download the file straight into /group-name/, where it stays until reviewed.
            // The download streams to disk, so a large video is never held in memory.
            var downloadUrl = file.UrlPrivateDownload ?? file.UrlPrivate ?? $"sim://{file.Name}";
            var storedFileName = SubmissionFileName.Build(group, assignmentNumber, file.Name, postedAt.ToGameTime(), FileIndex(supportedFiles, file));
            string storagePath;
            try
            {
                await using var stream = await _slackClient.DownloadFileAsync(downloadUrl, file.Name, ct);
                storagePath = await _fileStorageService.SaveInitialFileAsync(group.Name, storedFileName, stream, ct);
            }
            // A timeout also throws OperationCanceledException; only a stop of the app itself ends processing
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not download {FileName} from message {Ts} in {Channel}", file.Name, messageTs, channelId);
                failedFileNames.Add(file.Name);
                continue;
            }

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
                SlackFileId = string.IsNullOrWhiteSpace(file.Id) ? null : file.Id,
                Submitter = messageEvent.User ?? "Unknown",
                SubmitterName = submitterName,
                FileName = file.Name,
                FileSizeBytes = file.Size > 0 ? file.Size : new FileInfo(storagePath).Length,
                ContentType = file.Mimetype ?? "application/octet-stream",
                MessageText = messageEvent.Text,
                CreatedAt = postedAt
            };

            submission.MarkPending(storagePath, assignmentNumber);

            try
            {
                await _submissionRepository.AddAsync(submission, ct);
            }
            catch (DuplicateSubmissionException)
            {
                // The same event was handled at the same time elsewhere and won the race: drop this copy
                _logger.LogInformation("Submission already registered for file {FileName} in message {Ts}", file.Name, messageTs);
                await _fileStorageService.DeleteFileAsync(storagePath, ct);
                storedFileNames.Remove(Path.GetFileName(storagePath));
                continue;
            }

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

    /// <summary>The files of a message that are stored, in the order Slack lists them.</summary>
    private List<SlackFileItemDto> SupportedFiles(IEnumerable<SlackFileItemDto> files) =>
        files.Where(f => !string.IsNullOrWhiteSpace(f.Name) && !f.IsDeleted && IsSupportedFile(f)).ToList();

    /// <summary>
    /// Several files in one message are numbered (1), (2), ... in the order Slack lists them. The number comes from
    /// the message, not from the disk, so it stays the same when a message is processed again or rebuilt.
    /// </summary>
    private static int? FileIndex(IReadOnlyList<SlackFileItemDto> supportedFiles, SlackFileItemDto file)
    {
        if (supportedFiles.Count < 2) return null;

        for (var i = 0; i < supportedFiles.Count; i++)
        {
            if (file.Id != null ? supportedFiles[i].Id == file.Id : ReferenceEquals(supportedFiles[i], file)) return i + 1;
        }

        return null;
    }

    public async Task<ProcessSlackMessageResult> ProcessSlackMessageChangedAsync(SlackEventItemDto changeEvent, CancellationToken ct = default)
    {
        var channelId = changeEvent.Channel;
        var edited = changeEvent.Message;
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(edited?.Ts))
        {
            return ProcessSlackMessageResult.Ignored("Edit event without channel or message");
        }

        if (edited.BotId != null)
        {
            return ProcessSlackMessageResult.Ignored("Edited message is from a bot");
        }

        var files = edited.Files ?? new List<SlackFileItemDto>();
        var previous = changeEvent.PreviousMessage;

        // Files deleted from the message no longer count
        var deletedFileIds = files.Where(f => f.IsDeleted && f.Id != null).Select(f => f.Id!).ToHashSet();
        var existing = await _submissionRepository.GetBySlackMessageIdAsync(channelId, edited.Ts, ct);
        var removed = existing.Where(s => s.SlackFileId != null && deletedFileIds.Contains(s.SlackFileId)).ToList();
        if (removed.Count > 0)
        {
            await RemoveSubmissionsAsync(removed, ct);
            existing = existing.Except(removed).ToList();
            if (existing.Count == 0)
            {
                return ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>(), $"Removed {removed.Count} submission(s) of deleted files");
            }
        }

        // Slack also sends an edit when a thread reply is added or a link preview appears: only text changes matter
        if (removed.Count == 0 && previous != null && edited.Text == previous.Text)
        {
            return ProcessSlackMessageResult.Ignored("Message text did not change");
        }

        // Ask the original message's question again; channel and time come from the event, not from the edit
        var message = new SlackEventItemDto
        {
            Type = "message",
            Channel = channelId,
            User = edited.User ?? previous?.User,
            Text = edited.Text,
            Ts = edited.Ts,
            Files = files
        };

        if (existing.Count == 0)
        {
            // Skipped before, e.g. for a missing assignment number: try again with the new text
            var result = await ProcessSlackMessageAsync(message, ct);
            if (result.CreatedSubmissionIds.Count > 0)
            {
                await WithdrawNumberFeedbackAsync(channelId, edited.Ts, ct);
            }

            return result;
        }

        return await ReassignAsync(existing, message, ct);
    }

    /// <summary>The scout corrected the assignment number: the submissions and their file names follow.</summary>
    private async Task<ProcessSlackMessageResult> ReassignAsync(IReadOnlyList<Submission> submissions, SlackEventItemDto message, CancellationToken ct)
    {
        var parsed = _assignmentParser.Parse(message.Text);
        if (!parsed.Success || !parsed.AssignmentNumber.HasValue
            || await _assignmentRepository.GetByNumberAsync(parsed.AssignmentNumber.Value, ct) == null)
        {
            // Keep the number that was valid; a typo in an edit must not lose a registered submission
            _logger.LogInformation("Edit of message {Ts} has no valid assignment number; submissions keep #{Number}",
                message.Ts, submissions[0].AssignmentNumber);
            return ProcessSlackMessageResult.Ignored("Edited text has no valid assignment number; nothing changed");
        }

        var number = parsed.AssignmentNumber.Value;
        if (submissions.All(s => s.AssignmentNumber == number))
        {
            return ProcessSlackMessageResult.Ignored($"Assignment #{number} did not change");
        }

        var supportedFiles = SupportedFiles(message.Files ?? new List<SlackFileItemDto>());
        foreach (var submission in submissions)
        {
            var group = submission.Group ?? await _groupRepository.GetByIdAsync(submission.GroupId, ct);
            var storagePath = submission.StoragePath;
            if (group != null)
            {
                var file = supportedFiles.FirstOrDefault(f => f.Id != null && f.Id == submission.SlackFileId)
                           ?? new SlackFileItemDto { Name = submission.FileName };
                var newName = SubmissionFileName.Build(group, number, submission.FileName, submission.CreatedAt.ToGameTime(), FileIndex(supportedFiles, file));
                storagePath = await _fileStorageService.RenameFileAsync(submission.StoragePath, newName, ct);
            }

            var previousNumber = submission.AssignmentNumber;
            submission.Reassign(number, storagePath);
            await _submissionRepository.UpdateAsync(submission, ct);
            _logger.LogInformation("Submission {Id} moved from assignment #{From} to #{To} after an edit in Slack", submission.Id, previousNumber, number);
        }

        _changeNotifier.NotifyChanged();
        return ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>(), $"Moved {submissions.Count} submission(s) to assignment #{number}");
    }

    /// <summary>The scout fixed the number: the bot's question mark no longer applies.</summary>
    private async Task WithdrawNumberFeedbackAsync(string channel, string messageTs, CancellationToken ct)
    {
        var feedback = _options.Value.SlackFeedback ?? new SlackFeedbackConfig();
        var reactions = new[] { feedback.MissingAssignmentNumber?.Reaction, feedback.UnknownAssignmentNumber?.Reaction }
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct();
        foreach (var reaction in reactions)
        {
            await _slackClient.RemoveReactionAsync(channel, messageTs, reaction!, ct);
        }
    }

    public async Task<ProcessSlackMessageResult> ProcessSlackMessageDeletedAsync(SlackEventItemDto deleteEvent, CancellationToken ct = default)
    {
        var channelId = deleteEvent.Channel;
        var messageTs = deleteEvent.DeletedTs ?? deleteEvent.PreviousMessage?.Ts;
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageTs))
        {
            return ProcessSlackMessageResult.Ignored("Delete event without channel or message");
        }

        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(channelId, messageTs, ct);
        if (submissions.Count == 0)
        {
            return ProcessSlackMessageResult.Ignored($"No submission for channel {channelId}, ts {messageTs}");
        }

        await RemoveSubmissionsAsync(submissions, ct);
        return ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>(), $"Removed {submissions.Count} submission(s) of a deleted message");
    }

    /// <summary>Removes submissions whose message or file was deleted in Slack. Their files move to "_deleted".</summary>
    private async Task RemoveSubmissionsAsync(IReadOnlyList<Submission> submissions, CancellationToken ct)
    {
        foreach (var submission in submissions)
        {
            var group = submission.Group ?? await _groupRepository.GetByIdAsync(submission.GroupId, ct);
            var movedTo = await _fileStorageService.MoveToDeletedAsync(group?.Name ?? "unknown-group", submission.StoragePath, ct);
            _logger.LogInformation("Removed submission {Id} (assignment #{Number}) deleted in Slack; file kept at {Path}",
                submission.Id, submission.AssignmentNumber, movedTo ?? "(no file)");
        }

        await _submissionRepository.DeleteAsync(submissions, ct);
        _changeNotifier.NotifyChanged();
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

        // Same rule as removing an emoji and rebuilding: when reviewers disagree, the best review counts,
        // whatever order they reacted in. Without Slack (simulation, or reactions.get failed) only this emoji is known.
        var current = await _slackClient.GetReactionsAsync(targetChannel, targetTs, ct);
        var review = ChooseReview(WithReaction(current, reaction, reviewer!))!;

        await ApplyStatusAsync(submissions, review.Status, review.Reviewer, review.Emoji, ct);

        _logger.LogInformation("Processed reaction '{Reaction}' on message {Ts}: {Count} submission(s) now {Status}",
            reaction, targetTs, submissions.Count, review.Status);

        return ProcessSlackReactionResult.Succeeded(submissions[0].Id, review.Status);
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

    public async Task<ProcessSlackMessageResult> ProcessMissedMessageAsync(SlackEventItemDto message, CancellationToken ct = default)
    {
        var result = await ProcessSlackMessageAsync(message, ct);

        // It may have been edited to add the number while the app was away
        if (result.CreatedSubmissionIds.Count > 0 && !string.IsNullOrWhiteSpace(message.Channel) && !string.IsNullOrWhiteSpace(message.Ts))
        {
            await WithdrawNumberFeedbackAsync(message.Channel, message.Ts, ct);
        }

        return result;
    }

    public const string CatchUpReviewer = "Slack catch-up";

    public async Task<bool> SyncReviewAsync(string channelId, string messageTs, IReadOnlyList<SlackMessageReactionDto>? reactions, CancellationToken ct = default)
    {
        if (_runtime.ManualMode) return false;

        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(channelId, messageTs, ct);
        if (submissions.Count == 0) return false;

        var review = ChooseReview(reactions);
        if (review != null)
        {
            if (submissions.All(s => s.Status == review.Status)) return false;

            await ApplyStatusAsync(submissions, review.Status, review.Reviewer, review.Emoji, ct);
            return true;
        }

        // Reviews come newest first; an override has no emoji in Slack, so Slack showing none says nothing about it
        var takenBack = submissions
            .Where(s => s.Status is not (SubmissionStatus.Pending or SubmissionStatus.Received)
                        && s.Reviews.FirstOrDefault()?.Reviewer != DashboardOverrideReviewer)
            .ToList();
        if (takenBack.Count == 0) return false;

        await ApplyStatusAsync(takenBack, SubmissionStatus.Pending, CatchUpReviewer, emoji: null, ct, notes: "Review emoji removed");
        return true;
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

    /// <summary>
    /// The reactions on a message, including one that was just added. Slack may not list it yet,
    /// e.g. when the event is handled right after the reaction was placed.
    /// </summary>
    private static IReadOnlyList<SlackMessageReactionDto> WithReaction(IReadOnlyList<SlackMessageReactionDto>? reactions, string emoji, string user)
    {
        var list = (reactions ?? Array.Empty<SlackMessageReactionDto>()).ToList();
        var listed = list.Any(r => string.Equals(r.Name?.Trim(':'), emoji, StringComparison.OrdinalIgnoreCase)
                                   && (r.Users?.Contains(user) ?? false));
        if (!listed)
        {
            list.Add(new SlackMessageReactionDto { Name = emoji, Users = new List<string> { user } });
        }

        return list;
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
