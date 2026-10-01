using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Domain.Interfaces;

public interface ISubmissionRepository
{
    Task<Submission?> GetByIdAsync(Guid id, CancellationToken ct = default);
    /// <summary>All submissions from one Slack message: a message can carry several files.</summary>
    Task<IReadOnlyList<Submission>> GetBySlackMessageIdAsync(string channelId, string messageId, CancellationToken ct = default);
    Task<IReadOnlyList<Submission>> SearchSubmissionsAsync(SubmissionFilter filter, CancellationToken ct = default);
    Task<SubmissionStatistics> GetStatisticsAsync(CancellationToken ct = default);
    /// <exception cref="Exceptions.DuplicateSubmissionException">The same Slack file is already registered.</exception>
    Task AddAsync(Submission submission, CancellationToken ct = default);
    Task UpdateAsync(Submission submission, CancellationToken ct = default);
    /// <summary>
    /// Whether a file from a Slack message is already registered. Matched on Slack's file ID when known,
    /// so two files with the same name in one message both count; otherwise on the file name.
    /// </summary>
    Task<bool> ExistsAsync(string channelId, string messageId, string? fileId, string fileName, CancellationToken ct = default);

    /// <summary>Removes these submissions and their reviews, e.g. when the Slack message was deleted.</summary>
    Task DeleteAsync(IReadOnlyList<Submission> submissions, CancellationToken ct = default);

    /// <summary>Removes every submission and review. Used when rebuilding from Slack.</summary>
    Task DeleteAllAsync(CancellationToken ct = default);

    /// <summary>Removes every submission and review of one group. Used when rebuilding one group from Slack.</summary>
    Task DeleteByGroupAsync(Guid groupId, CancellationToken ct = default);
}
