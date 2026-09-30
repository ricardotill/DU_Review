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
    Task AddAsync(Submission submission, CancellationToken ct = default);
    Task UpdateAsync(Submission submission, CancellationToken ct = default);
    Task<bool> ExistsAsync(string channelId, string messageId, string fileName, CancellationToken ct = default);

    /// <summary>Removes every submission and review. Used when rebuilding from Slack.</summary>
    Task DeleteAllAsync(CancellationToken ct = default);
}
