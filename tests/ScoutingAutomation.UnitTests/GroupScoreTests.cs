using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using Xunit;

namespace ScoutingAutomation.UnitTests;

public class GroupScoreTests
{
    [Fact]
    public async Task GetStatisticsAsync_ShouldScoreBestReviewedSubmissionPerAssignment()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<ScoutingDbContext>()
            .UseInMemoryDatabase($"GroupScore_{Guid.NewGuid():N}")
            .Options;
        await using var context = new ScoutingDbContext(options);

        var group = new Group { Name = "De Wolven", SlackChannelId = "C03WOLVEN" };
        context.Groups.Add(group);
        context.Assignments.AddRange(
            new Assignment { Number = 1, StandardPoints = 1, AwesomePoints = 2 },
            new Assignment { Number = 2, StandardPoints = 2, AwesomePoints = 4 },
            new Assignment { Number = 3, StandardPoints = 1, AwesomePoints = 2 },
            new Assignment { Number = 4, StandardPoints = 2, AwesomePoints = 4 });

        Submission Sub(int? number, SubmissionStatus status) =>
            new() { GroupId = group.Id, AssignmentNumber = number, Status = status };

        context.Submissions.AddRange(
            Sub(1, SubmissionStatus.Approved),  // 1
            Sub(1, SubmissionStatus.Approved),  // duplicate, counts once
            Sub(2, SubmissionStatus.Approved),
            Sub(2, SubmissionStatus.Awesome),   // best of assignment 2: 4
            Sub(3, SubmissionStatus.Rejected),  // 0
            Sub(4, SubmissionStatus.Pending),   // 0
            Sub(99, SubmissionStatus.Approved), // unknown assignment: 0
            Sub(null, SubmissionStatus.Approved));
        await context.SaveChangesAsync();

        var repository = new SubmissionRepository(context);

        // Act
        var stats = await repository.GetStatisticsAsync();

        // Assert
        stats.GroupStatistics.Single(g => g.GroupId == group.Id).Score.Should().Be(5);
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldMergeResubmissionsPerGroupAndAssignment()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<ScoutingDbContext>()
            .UseInMemoryDatabase($"Metrics_{Guid.NewGuid():N}")
            .Options;
        await using var context = new ScoutingDbContext(options);

        var group = new Group { Name = "De Bosuilen", SlackChannelId = "C02BOSUILEN" };
        context.Groups.Add(group);
        context.Assignments.AddRange(
            new Assignment { Number = 1, StandardPoints = 1, AwesomePoints = 2 },
            new Assignment { Number = 2, StandardPoints = 2, AwesomePoints = 4 },
            new Assignment { Number = 3, StandardPoints = 1, AwesomePoints = 2 });

        Submission Sub(int? number, SubmissionStatus status) =>
            new() { GroupId = group.Id, AssignmentNumber = number, Status = status };

        context.Submissions.AddRange(
            Sub(1, SubmissionStatus.Rejected),
            Sub(1, SubmissionStatus.Approved),  // assignment 1: approved
            Sub(2, SubmissionStatus.Rejected),
            Sub(2, SubmissionStatus.Pending),   // assignment 2: pending
            Sub(3, SubmissionStatus.Rejected),
            Sub(3, SubmissionStatus.Rejected),  // assignment 3: rejected once
            Sub(null, SubmissionStatus.Pending)); // unresolved
        await context.SaveChangesAsync();

        var repository = new SubmissionRepository(context);

        // Act
        var stats = await repository.GetStatisticsAsync();

        // Assert
        stats.TotalSubmissions.Should().Be(3);
        stats.ApprovedCount.Should().Be(1);
        stats.PendingCount.Should().Be(1);
        stats.RejectedCount.Should().Be(1);
        stats.UnresolvedCount.Should().Be(1);

        var groupStat = stats.GroupStatistics.Single(g => g.GroupId == group.Id);
        groupStat.TotalSubmissions.Should().Be(3);
        groupStat.RejectedCount.Should().Be(1);
        groupStat.Score.Should().Be(1);

        var assignment1 = stats.AssignmentStatistics.Single(a => a.AssignmentNumber == 1);
        assignment1.TotalSubmissions.Should().Be(1);
        assignment1.ApprovedCount.Should().Be(1);
        assignment1.RejectedCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatisticsAsync_EqualScores_ShouldShareRank()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<ScoutingDbContext>()
            .UseInMemoryDatabase($"Rank_{Guid.NewGuid():N}")
            .Options;
        await using var context = new ScoutingDbContext(options);

        var a = new Group { Name = "A", SlackChannelId = "CA" };
        var b = new Group { Name = "B", SlackChannelId = "CB" };
        var c = new Group { Name = "C", SlackChannelId = "CC" };
        context.Groups.AddRange(a, b, c);
        context.Assignments.Add(new Assignment { Number = 1, StandardPoints = 2, AwesomePoints = 4 });
        context.Submissions.AddRange(
            new Submission { GroupId = a.Id, AssignmentNumber = 1, Status = SubmissionStatus.Approved },
            new Submission { GroupId = b.Id, AssignmentNumber = 1, Status = SubmissionStatus.Approved });
        await context.SaveChangesAsync();

        // Act
        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        // Assert
        stats.GroupStatistics.Single(g => g.GroupId == a.Id).Rank.Should().Be(1);
        stats.GroupStatistics.Single(g => g.GroupId == b.Id).Rank.Should().Be(1);
        stats.GroupStatistics.Single(g => g.GroupId == c.Id).Rank.Should().Be(3);
    }
}
