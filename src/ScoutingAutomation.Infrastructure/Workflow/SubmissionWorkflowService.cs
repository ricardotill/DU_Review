using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Exceptions;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Storage;

namespace ScoutingAutomation.Infrastructure.Workflow;

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
    private readonly SlackLinks? _links;
    private readonly SlackChannelLocks? _channels;
    private readonly SlackProcessingGate? _gate;
    private readonly SlackOwnUploads? _ownUploads;

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
        ILogger<SubmissionWorkflowService> logger,
        SlackLinks? links = null,
        SlackChannelLocks? channels = null,
        SlackProcessingGate? gate = null,
        SlackOwnUploads? ownUploads = null)
    {
        _channels = channels;
        _gate = gate;
        _ownUploads = ownUploads;
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
        _links = links;
    }

    public Task<ProcessSlackMessageResult> ProcessSlackMessageAsync(SlackEventItemDto messageEvent, CancellationToken ct = default) =>
        ProcessMessageAsync(messageEvent, silent: false, manual: _runtime.ManualMode, ct);

    public Task<ProcessSlackMessageResult> ImportSlackMessageAsync(SlackEventItemDto message, CancellationToken ct = default)
    {
        // The bot blocked this attempt live, and nothing was stored then. A rebuild applies reviews only after
        // importing every message, so it cannot see that the assignment was already done: the bot's emoji tells.
        var blocked = _options.Value.SlackFeedback?.AlreadyCompleted?.Reaction?.Trim(':');
        if (!string.IsNullOrWhiteSpace(blocked)
            && (message.Reactions ?? new List<SlackMessageReactionDto>()).Any(r => string.Equals(r.Name?.Trim(':'), blocked, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("Skipped message {Ts} in {Channel}: it was blocked live as a new attempt after approval", message.Ts, message.Channel);
            return Task.FromResult(ProcessSlackMessageResult.Ignored("Nieuwe poging na goedkeuring; was al geweigerd"));
        }

        return ProcessMessageAsync(message, silent: true, manual: false, ct);
    }

    /// <param name="silent">True when importing history: Slack already saw this message, so no feedback is posted again.</param>
    /// <param name="manual">Manual mode: only download and name the files; no submissions, reviews or Slack feedback.</param>
    private async Task<ProcessSlackMessageResult> ProcessMessageAsync(SlackEventItemDto messageEvent, bool silent, bool manual, CancellationToken ct)
    {
        silent |= manual;

        var channelId = messageEvent.Channel;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            _logger.LogWarning("Incoming message missing channel ID");
            return ProcessSlackMessageResult.Ignored("Bericht zonder kanaal-ID");
        }

        // Messages from before the event never count; no feedback either, the game had not started
        if (_runtime.IsBeforeEventStart(messageEvent.Ts))
        {
            _logger.LogDebug("Ignored message {Ts} in {ChannelId}: posted before the event start", messageEvent.Ts, channelId);
            return ProcessSlackMessageResult.Ignored("Bericht van vóór de starttijd");
        }

        // FR-001: Ignore unknown channels
        var group = await _groupRepository.GetBySlackChannelIdAsync(channelId, ct);
        if (group == null)
        {
            _logger.LogInformation("Ignored message from unknown channel {ChannelId}", channelId);
            return ProcessSlackMessageResult.Ignored($"Kanaal {channelId} hoort bij geen enkele groep");
        }

        // Reviewers post in group channels too, but their messages are never submissions
        if (_runtime.IsReviewer(messageEvent.User))
        {
            _logger.LogDebug("Ignored message from reviewer {User} in {ChannelId}", messageEvent.User, channelId);
            return ProcessSlackMessageResult.Ignored($"Gebruiker '{messageEvent.User}' is jurylid");
        }

        // The bot's own messages, such as a picture the jury sent from the app, are never submissions.
        // A shared file does not always carry bot_id, but it does carry the bot's user ID.
        if (messageEvent.BotId != null || await IsOwnBotAsync(messageEvent.User, ct))
        {
            _logger.LogDebug("Ignored message from bot {BotId} in {ChannelId}", messageEvent.BotId, channelId);
            return ProcessSlackMessageResult.Ignored("Bericht is van een bot");
        }

        // Check attachments
        var files = messageEvent.Files;
        if (files == null || files.Count == 0)
        {
            _logger.LogDebug("Message in {ChannelId} has no attached files, ignoring", channelId);
            return ProcessSlackMessageResult.Ignored("Geen bestanden in het bericht");
        }

        // Texts can be changed in the app while it runs
        var feedback = _runtime.Feedback;

        // A submission posted after the event ended never counts; the message's own time decides,
        // so an edit or a catch-up after the end does not drop a submission that was on time
        if (_runtime.IsAfterEventEnd(messageEvent.Ts))
        {
            _logger.LogInformation("Skipped message {Ts} in {Channel}: posted after the event end", messageEvent.Ts, channelId);
            await SendFeedbackAsync(silent, feedback.TooLate, channelId, messageEvent.Ts, "{end}", _runtime.EventEnd?.ToGameTime().ToString("HH:mm"), ct);
            return ProcessSlackMessageResult.Ignored("Bericht van na de eindtijd");
        }

        // FR-002: Parse assignment number
        // Without a clear assignment number nothing is stored: the scout is asked to post again
        var parseResult = _assignmentParser.Parse(messageEvent.Text);
        if (!parseResult.Success || parseResult.AssignmentCode == null)
        {
            _logger.LogWarning("Skipped message '{Text}' in channel {Channel}: {Reason}",
                messageEvent.Text, channelId, parseResult.ErrorMessage);
            await SendFeedbackAsync(silent, feedback.MissingAssignmentNumber, channelId, messageEvent.Ts, null, null, ct);
            return ProcessSlackMessageResult.Ignored(parseResult.ErrorMessage ?? "Geen opdrachtnummer gevonden");
        }

        var assignmentCode = parseResult.AssignmentCode;
        if (await _assignmentRepository.GetByCodeAsync(assignmentCode, ct) == null)
        {
            _logger.LogWarning("Skipped message in {Channel}: unknown assignment {Code}", channelId, assignmentCode);

            // Letters that are no special assignment are most likely just a word ("opdracht is klaar"): ask for the number
            var reply = AssignmentCode.IsLetter(assignmentCode) ? feedback.MissingAssignmentNumber : feedback.UnknownAssignmentNumber;
            await SendFeedbackAsync(silent, reply, channelId, messageEvent.Ts, "{number}", assignmentCode, ct);
            return ProcessSlackMessageResult.Ignored($"Opdracht {assignmentCode} bestaat niet");
        }

        // A group that already has this assignment approved or awesome may not try again for more points
        if (!manual && await IsNewAttemptAfterApprovalAsync(group.Id, assignmentCode, channelId, messageEvent.Ts, ct))
        {
            _logger.LogInformation("Skipped message {Ts} in {Channel}: group {Group} already completed assignment {Number}",
                messageEvent.Ts, channelId, group.Name, assignmentCode);
            await SendFeedbackAsync(silent, feedback.AlreadyCompleted, channelId, messageEvent.Ts, "{number}", assignmentCode, ct);
            return ProcessSlackMessageResult.Ignored($"Opdracht {assignmentCode} is al goedgekeurd voor deze groep");
        }

        var createdIds = new List<Guid>();
        var unsupportedFileNames = new List<string>();
        var failedFileNames = new List<string>();
        var storedFileNames = new List<string>();
        var messageTs = messageEvent.Ts ?? $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        // Feedback emoji placed while handling this message; a second reply with the same emoji is still posted
        var reactedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            // A picture the jury sent from the app, also before the bot's own user ID is known
            if (file.Id != null && (_ownUploads?.Contains(file.Id) ?? false))
            {
                _logger.LogDebug("Ignored file {FileName}: the app uploaded it", file.Name);
                continue;
            }

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
            // Connected to Slack, a file without an address (hidden by a plan limit, or still being processed)
            // cannot be fetched: storing a stand-in would hide a lost submission
            var downloadUrl = file.UrlPrivateDownload ?? file.UrlPrivate ?? (_slackClient.IsConnected ? null : $"sim://{file.Name}");
            var storedFileName = SubmissionFileName.Build(group, assignmentCode, file.Name, postedAt.ToGameTime(), FileIndex(supportedFiles, file));
            string storagePath;
            try
            {
                if (downloadUrl == null)
                {
                    throw new HttpRequestException($"Slack gave no download address for {file.Name}");
                }

                await using var stream = await _slackClient.DownloadFileAsync(downloadUrl, file.Name, ct);
                storagePath = await _fileStorageService.SaveInitialFileAsync(group.Name, storedFileName, stream, ct);
            }
            // A timeout also throws OperationCanceledException; only a stop of the app itself ends processing
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (IsDiskFull(ex))
                {
                    _logger.LogCritical(ex, "The disk is full: could not store {FileName} from message {Ts} in {Channel}", file.Name, messageTs, channelId);
                }
                else
                {
                    _logger.LogWarning(ex, "Could not download {FileName} from message {Ts} in {Channel}", file.Name, messageTs, channelId);
                }

                // Manual mode remembers what it saved: forget this file, so a next try downloads it again
                if (manual) _runtime.ReleaseManualDownload(channelId, messageTs, file.Id ?? file.Name);
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
                FileName = file.Name.Length > MaxFileNameLength ? file.Name[..MaxFileNameLength] : file.Name,
                FileSizeBytes = file.Size > 0 ? file.Size : new FileInfo(storagePath).Length,
                ContentType = file.Mimetype ?? "application/octet-stream",
                MessageText = messageEvent.Text,
                CreatedAt = postedAt
            };

            submission.MarkPending(storagePath, assignmentCode);

            try
            {
                await _submissionRepository.AddAsync(submission, ct);
            }
            catch (DuplicateSubmissionException)
            {
                // The same event was handled at the same time elsewhere and won the race: drop this copy
                _logger.LogInformation("Submission already registered for file {FileName} in message {Ts}", file.Name, messageTs);
                await _fileStorageService.DeleteFileAsync(storagePath, CancellationToken.None);
                storedFileNames.Remove(Path.GetFileName(storagePath));
                continue;
            }
            catch
            {
                // Not saved: the file must not stay behind without a submission. The catch-up tries the message again.
                await _fileStorageService.DeleteFileAsync(storagePath, CancellationToken.None);
                throw;
            }

            createdIds.Add(submission.Id);

            _logger.LogInformation("Registered submission {Id} for group {Group}, assignment #{Assignment}, file {File}",
                submission.Id, group.Name, assignmentCode, file.Name);
        }

        if (createdIds.Count > 0)
        {
            _changeNotifier.NotifyChanged();

            // A rebuild imports old messages: those are not news
            if (!silent)
            {
                _changeNotifier.NotifyCreated(new SubmissionCreatedNotification(
                    group.Id,
                    group.Name,
                    group.Abbreviation,
                    assignmentCode,
                    string.IsNullOrWhiteSpace(submitterName) ? messageEvent.User ?? "Onbekend" : submitterName,
                    createdIds,
                    postedAt));
            }
        }

        if (unsupportedFileNames.Count > 0)
        {
            await SendFeedbackAsync(silent, feedback.UnsupportedFile, channelId, messageEvent.Ts, "{files}", string.Join(", ", unsupportedFileNames), ct, reactedNow);
        }

        if (failedFileNames.Count > 0)
        {
            await SendFeedbackAsync(silent, feedback.DownloadFailed, channelId, messageEvent.Ts, "{files}", string.Join(", ", failedFileNames), ct, reactedNow);
        }

        var summary = manual
            ? $"Handmatige modus: {storedFileNames.Count} bestand(en) opgeslagen"
            : $"{createdIds.Count} inzending(en) geregistreerd";
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
            return ProcessSlackMessageResult.Ignored("Bewerking zonder kanaal of bericht");
        }

        if (edited.BotId != null || await IsOwnBotAsync(edited.User, ct))
        {
            return ProcessSlackMessageResult.Ignored("Bewerkt bericht is van een bot");
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
                return ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>(), $"{removed.Count} inzending(en) van verwijderde bestanden weggehaald");
            }
        }

        // Slack also sends an edit when a thread reply is added or a link preview appears: only text changes matter
        if (removed.Count == 0 && previous != null && edited.Text == previous.Text)
        {
            return ProcessSlackMessageResult.Ignored("Tekst van het bericht is niet veranderd");
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
        if (!parsed.Success || parsed.AssignmentCode == null
            || await _assignmentRepository.GetByCodeAsync(parsed.AssignmentCode, ct) == null)
        {
            // Keep the number that was valid; a typo in an edit must not lose a registered submission
            _logger.LogInformation("Edit of message {Ts} has no valid assignment number; submissions keep #{Number}",
                message.Ts, submissions[0].AssignmentCode);
            return ProcessSlackMessageResult.Ignored("Bewerkte tekst heeft geen geldig opdrachtnummer; er is niets veranderd");
        }

        var number = parsed.AssignmentCode;
        if (submissions.All(s => s.AssignmentCode == number))
        {
            return ProcessSlackMessageResult.Ignored($"Opdracht {number} is niet veranderd");
        }

        // Moving to an assignment the group already completed would be a new attempt after approval
        if (await IsNewAttemptAfterApprovalAsync(submissions[0].GroupId, number, submissions[0].SlackChannelId, message.Ts, ct, isRegistered: true))
        {
            _logger.LogInformation("Edit of message {Ts} not applied: the group already completed assignment {Number}", message.Ts, number);
            await SendFeedbackAsync(silent: false, _runtime.Feedback.AlreadyCompleted, submissions[0].SlackChannelId, message.Ts, "{number}", number, ct);
            return ProcessSlackMessageResult.Ignored($"Opdracht {number} is al goedgekeurd voor deze groep; er is niets veranderd");
        }

        var supportedFiles = SupportedFiles(message.Files ?? new List<SlackFileItemDto>());
        foreach (var submission in submissions)
        {
            var group = submission.Group ?? await _groupRepository.GetByIdAsync(submission.GroupId, ct);
            var previousPath = submission.StoragePath;
            var storagePath = previousPath;
            if (group != null)
            {
                var file = supportedFiles.FirstOrDefault(f => f.Id != null && f.Id == submission.SlackFileId)
                           ?? new SlackFileItemDto { Name = submission.FileName };
                var newName = SubmissionFileName.Build(group, number, submission.FileName, submission.CreatedAt.ToGameTime(), FileIndex(supportedFiles, file));
                storagePath = await _fileStorageService.RenameFileAsync(submission.StoragePath, newName, ct);
            }

            var previousNumber = submission.AssignmentCode;
            submission.Reassign(number, storagePath);
            try
            {
                await _submissionRepository.UpdateAsync(submission, ct);
            }
            catch
            {
                // The database still has the old name: the file gets it back
                await UndoMoveAsync(storagePath, previousPath);
                throw;
            }
            _logger.LogInformation("Submission {Id} moved from assignment #{From} to #{To} after an edit in Slack", submission.Id, previousNumber, number);
        }

        _changeNotifier.NotifyChanged();
        return ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>(), $"{submissions.Count} inzending(en) verplaatst naar opdracht {number}");
    }

    /// <summary>
    /// Whether this message is a new attempt at an assignment the group already completed: blocking is on, the message
    /// is not registered yet, and another message of the group posted <em>earlier</em> has it approved or awesome.
    /// A message that is already registered (the catch-up reads it again) or that was posted before the approval
    /// (an earlier attempt the app missed) is never blocked: it is not an attempt after the approval.
    /// </summary>
    /// <param name="isRegistered">True when the caller already knows the message has submissions, e.g. for an edit.</param>
    private async Task<bool> IsNewAttemptAfterApprovalAsync(Guid groupId, string assignmentCode, string channelId, string? messageTs, CancellationToken ct,
        bool isRegistered = false)
    {
        if (!_runtime.BlockAttemptsAfterApproval) return false;

        if (!isRegistered && !string.IsNullOrWhiteSpace(messageTs)
            && (await _submissionRepository.GetBySlackMessageIdAsync(channelId, messageTs, ct)).Count > 0)
        {
            return false;
        }

        // Without a timestamp of its own the message counts as the newest
        var hasTs = SlackTimestamp.ToDateTimeOffset(messageTs) != null;
        var existing = await _submissionRepository.SearchSubmissionsAsync(new SubmissionFilter { GroupId = groupId, AssignmentCode = assignmentCode }, ct);
        return existing.Any(s => s.Status is SubmissionStatus.Approved or SubmissionStatus.Awesome
                                 && !(s.SlackChannelId == channelId && s.SlackMessageId == messageTs)
                                 && (!hasTs || SlackTimestamp.IsBefore(s.SlackMessageId, messageTs)));
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
            return ProcessSlackMessageResult.Ignored("Verwijdering zonder kanaal of bericht");
        }

        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(channelId, messageTs, ct);
        if (submissions.Count == 0)
        {
            return ProcessSlackMessageResult.Ignored($"Geen inzending voor kanaal {channelId}, ts {messageTs}");
        }

        await RemoveSubmissionsAsync(submissions, ct);
        return ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>(), $"{submissions.Count} inzending(en) van een verwijderd bericht weggehaald");
    }

    /// <summary>Removes submissions whose message or file was deleted in Slack. Their files move to "_deleted".</summary>
    private async Task RemoveSubmissionsAsync(IReadOnlyList<Submission> submissions, CancellationToken ct)
    {
        var groupNames = new Dictionary<Guid, string>();
        foreach (var submission in submissions)
        {
            var group = submission.Group ?? await _groupRepository.GetByIdAsync(submission.GroupId, ct);
            groupNames[submission.Id] = group?.Name ?? "unknown-group";
        }

        // The rows go first: when that fails, nothing changed. A file that cannot be moved afterwards only stays
        // in the group folder, where no submission points to it any more.
        await _submissionRepository.DeleteAsync(submissions, ct);
        foreach (var submission in submissions)
        {
            try
            {
                var movedTo = await _fileStorageService.MoveToDeletedAsync(groupNames[submission.Id], submission.StoragePath, CancellationToken.None);
                _logger.LogInformation("Removed submission {Id} (assignment #{Number}) deleted in Slack; file kept at {Path}",
                    submission.Id, submission.AssignmentCode, movedTo ?? "(no file)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Removed submission {Id}, but could not move its file {Path} to the deleted folder", submission.Id, submission.StoragePath);
            }
        }

        _changeNotifier.NotifyChanged();
    }

    public async Task<ProcessSlackReactionResult> ProcessSlackReactionAsync(SlackEventItemDto reactionEvent, CancellationToken ct = default)
    {
        var reaction = reactionEvent.Reaction?.Trim(':').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(reaction))
        {
            return ProcessSlackReactionResult.Ignored("Lege emoji");
        }

        if (_runtime.ManualMode)
        {
            return ProcessSlackReactionResult.Ignored("Handmatige modus: reacties worden niet verwerkt");
        }

        // Only reactions from configured reviewers may change a submission's status
        var reviewer = reactionEvent.User;
        if (!_runtime.IsReviewer(reviewer))
        {
            _logger.LogInformation("Ignored reaction '{Reaction}' from non-reviewer {User}", reaction, reviewer);
            return ProcessSlackReactionResult.Ignored($"Gebruiker '{reviewer}' is geen jurylid");
        }

        var targetChannel = reactionEvent.Item?.Channel;
        var targetTs = reactionEvent.Item?.Ts;

        if (string.IsNullOrWhiteSpace(targetChannel) || string.IsNullOrWhiteSpace(targetTs))
        {
            return ProcessSlackReactionResult.Failed("Reactie zonder kanaal of tijdstempel van het bericht");
        }

        // FR-007: Review status reactions mapping
        var targetStatus = ReviewPolicy.StatusFor(_options.Value, reaction);
        if (!targetStatus.HasValue)
        {
            _logger.LogDebug("Reaction '{Reaction}' is not mapped to any review status", reaction);
            return ProcessSlackReactionResult.Ignored($"Emoji '{reaction}' is geen beoordeling");
        }

        // A message can carry several files: the reaction reviews all of them
        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(targetChannel, targetTs, ct);
        // A message without a stored file (no photo, deleted, or skipped for its number) has nothing to review.
        // Not a failure: should it get a submission later, the catch-up reads its reactions then.
        if (submissions.Count == 0)
        {
            _logger.LogInformation("Reaction '{Reaction}' on message {Ts} in {Channel} ignored: no submission belongs to it", reaction, targetTs, targetChannel);
            return ProcessSlackReactionResult.Ignored($"Geen inzending bij dit bericht (kanaal {targetChannel}, ts {targetTs})");
        }

        // Same rule as removing an emoji and rebuilding: when reviewers disagree, the best review counts,
        // whatever order they reacted in. Without Slack (simulation, or reactions.get failed) only this emoji is known.
        var current = await _slackClient.GetReactionsAsync(targetChannel, targetTs, ct);
        var review = ChooseReview(WithReaction(current, reaction, reviewer!))!;

        // Slack delivers an event again when it got no answer in time: the same review is not added twice
        if (submissions.All(s => s.Status == review.Status && s.Reviews.FirstOrDefault() is { } last
                                 && last.Reviewer == review.Reviewer && last.ReactionEmoji == review.Emoji))
        {
            return ProcessSlackReactionResult.Ignored($"Status blijft {review.Status.StatusLabel()}");
        }

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

        if (!WouldChange(submissions, ChooseReview(reactions))) return false;

        // The reactions were read with the channel's history, maybe minutes ago, and a reviewer may have reacted
        // since: a status only changes on what Slack shows now. When that cannot be read, nothing changes.
        if (_slackClient.IsConnected)
        {
            reactions = await _slackClient.GetReactionsAsync(channelId, messageTs, ct);
            if (reactions == null)
            {
                _logger.LogWarning("Could not read reactions on message {Ts} in {Channel} again; review left unchanged", messageTs, channelId);
                return false;
            }
        }

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

        await ApplyStatusAsync(takenBack, SubmissionStatus.Pending, CatchUpReviewer, emoji: null, ct, notes: "Beoordelingsemoji weggehaald");
        return true;
    }

    /// <summary>Whether <see cref="SyncReviewAsync"/> would change these submissions for this review.</summary>
    private static bool WouldChange(IReadOnlyList<Submission> submissions, ChosenReview? review) =>
        review != null
            ? submissions.Any(s => s.Status != review.Status)
            : submissions.Any(s => s.Status is not (SubmissionStatus.Pending or SubmissionStatus.Received)
                                   && s.Reviews.FirstOrDefault()?.Reviewer != DashboardOverrideReviewer);

    public async Task<ProcessSlackReactionResult> ProcessSlackReactionRemovedAsync(SlackEventItemDto reactionEvent, CancellationToken ct = default)
    {
        var reaction = reactionEvent.Reaction?.Trim(':').ToLowerInvariant();
        var channel = reactionEvent.Item?.Channel;
        var messageTs = reactionEvent.Item?.Ts;

        if (_runtime.ManualMode)
        {
            return ProcessSlackReactionResult.Ignored("Handmatige modus: reacties worden niet verwerkt");
        }

        // Only a reviewer taking back a review emoji can change a status
        if (!_runtime.IsReviewer(reactionEvent.User) || ReviewPolicy.StatusFor(_options.Value, reaction) == null)
        {
            return ProcessSlackReactionResult.Ignored($"Weghalen van '{reaction}' door '{reactionEvent.User}' verandert geen beoordeling");
        }

        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(messageTs))
        {
            return ProcessSlackReactionResult.Failed("Reactie zonder kanaal of tijdstempel van het bericht");
        }

        var submissions = await _submissionRepository.GetBySlackMessageIdAsync(channel, messageTs, ct);
        if (submissions.Count == 0)
        {
            return ProcessSlackReactionResult.Ignored($"Geen inzending voor kanaal {channel}, ts {messageTs}");
        }

        // Other reviewers may still have reacted: what counts is what is on the message now
        var remaining = await _slackClient.GetReactionsAsync(channel, messageTs, ct);
        if (remaining == null)
        {
            _logger.LogWarning("Could not read reactions on message {Ts} in {Channel}; status left unchanged", messageTs, channel);
            return ProcessSlackReactionResult.Failed("Reacties konden niet uit Slack worden gelezen; status ongewijzigd");
        }

        var review = ChooseReview(remaining);
        var newStatus = review?.Status ?? SubmissionStatus.Pending;
        if (submissions.All(s => s.Status == newStatus))
        {
            return ProcessSlackReactionResult.Ignored($"Status blijft {newStatus.StatusLabel()}");
        }

        if (review == null)
        {
            await ApplyStatusAsync(submissions, SubmissionStatus.Pending, reactionEvent.User!, reaction, ct, notes: "Beoordelingsemoji weggehaald");
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
            var previousPath = submission.StoragePath;
            var newStoragePath = await _fileStorageService.MoveFileToStatusAsync(groupName, previousPath, status, ct);
            submission.TransitionStatus(status, newStoragePath, reviewer, emoji, notes);
            try
            {
                await _submissionRepository.UpdateAsync(submission, ct);
            }
            catch
            {
                // The database still points to the old place: the file goes back there
                await UndoMoveAsync(newStoragePath, previousPath);
                throw;
            }
        }

        _changeNotifier.NotifyChanged();
    }

    /// <summary>Puts a file back at <paramref name="previousPath"/> after the database refused the change.</summary>
    private async Task UndoMoveAsync(string currentPath, string previousPath)
    {
        if (string.Equals(currentPath, previousPath, StringComparison.Ordinal)) return;

        try
        {
            if (!await _fileStorageService.FileExistsAsync(currentPath)) return;

            var restored = await _fileStorageService.MoveToPathAsync(currentPath, previousPath);
            if (!string.Equals(restored, previousPath, StringComparison.Ordinal))
            {
                _logger.LogError("File {Path} went back to {Restored}, not to {Previous}", currentPath, restored, previousPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not move file {Path} back to {Previous} after a failed save", currentPath, previousPath);
        }
    }

    public const string DashboardOverrideReviewer = "Dashboard override";

    public async Task<Submission> OverrideStatusAsync(Guid submissionId, SubmissionStatus newStatus, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for a status override", nameof(reason));
        }

        // A rebuild replaces every submission: an override now would be lost, or hit one that is gone
        if (_gate?.IsClosed == true)
        {
            throw new InvalidOperationException("Opnieuw opbouwen uit Slack loopt; pas de status aan als dat klaar is");
        }

        var submission = await _submissionRepository.GetByIdAsync(submissionId, ct)
            ?? throw new InvalidOperationException($"Submission {submissionId} not found");

        // Takes turns with the Slack events of this channel, so a reaction and the override never move the file at once
        using var turn = _channels == null ? null : await _channels.EnterAsync(submission.SlackChannelId, ct);
        reason = reason.Trim();
        if (reason.Length > MaxOverrideReasonLength) reason = reason[..MaxOverrideReasonLength];

        // Like an emoji in Slack, the override reviews every file of the message. Changing one photo of two would
        // leave the other at its old status, and since the best status counts, the points would not change.
        var siblings = await _submissionRepository.GetBySlackMessageIdAsync(submission.SlackChannelId, submission.SlackMessageId, ct);
        var targets = siblings.Any(s => s.Id == submission.Id)
            ? siblings.Select(s => s.Id == submission.Id ? submission : s).ToList()
            : new List<Submission> { submission };

        await ApplyStatusAsync(targets, newStatus, DashboardOverrideReviewer, emoji: null, ct, notes: reason);

        _logger.LogWarning("Submission {Id} status overridden from the dashboard to {Status} ({Count} file(s) of message {Ts}). Reason: {Reason}",
            submissionId, newStatus, targets.Count, submission.SlackMessageId, reason.ReplaceLineEndings(" "));
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

    /// <param name="reactedNow">Emoji placed while handling this message: a second reply with the same emoji is still posted.</param>
    private async Task SendFeedbackAsync(bool silent, FeedbackConfig? config, string channel, string? messageTs, string? placeholder, string? value,
        CancellationToken ct, ISet<string>? reactedNow = null)
    {
        if (silent || config == null || string.IsNullOrWhiteSpace(messageTs)) return;

        // A reaction that was already there means this message got feedback before. One that could not be placed
        // means no reply either: the catch-up tries again, and the scout does not get the same reply each time.
        var reaction = config.Reaction?.Trim(':');
        if (!string.IsNullOrWhiteSpace(reaction) && !(reactedNow?.Contains(reaction) ?? false))
        {
            if (!await _slackClient.AddReactionAsync(channel, messageTs, reaction, ct)) return;
            reactedNow?.Add(reaction);
        }

        if (!string.IsNullOrWhiteSpace(config.Message))
        {
            var text = placeholder != null ? config.Message.Replace(placeholder, value) : config.Message;
            await _slackClient.PostThreadReplyAsync(channel, messageTs, text, ct);
        }
    }

    /// <summary>The longest original file name stored; the database column holds 260 characters.</summary>
    public const int MaxFileNameLength = 260;

    /// <summary>The longest reason for an override that is stored.</summary>
    public const int MaxOverrideReasonLength = 1000;

    /// <summary>Whether storing failed because the disk is full (ENOSPC on Linux and macOS, ERROR_DISK_FULL on Windows).</summary>
    private static bool IsDiskFull(Exception ex) =>
        ex is IOException io && (io.HResult & 0xFFFF) is 28 or 112 or 39;

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

    /// <summary>Whether <paramref name="userId"/> is the bot itself; false while the bot's user ID is not known.</summary>
    private async Task<bool> IsOwnBotAsync(string? userId, CancellationToken ct)
    {
        if (_links == null || string.IsNullOrWhiteSpace(userId)) return false;
        await _links.EnsureLoadedAsync(ct);
        return _links.BotUserId == userId;
    }
}
