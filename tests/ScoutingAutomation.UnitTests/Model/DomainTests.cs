using FluentAssertions;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Model;

public class DomainTests
{
    [Theory]
    [InlineData(SubmissionStatus.Approved, 2)]
    [InlineData(SubmissionStatus.Awesome, 4)]
    [InlineData(SubmissionStatus.Pending, 0)]
    [InlineData(SubmissionStatus.Rejected, 0)]
    [InlineData(SubmissionStatus.Received, 0)]
    public void Assignment_PointsFor_ShouldMatchStatus(SubmissionStatus status, int expected)
    {
        new Assignment { StandardPoints = 2, AwesomePoints = 4 }.PointsFor(status).Should().Be(expected);
    }

    [Theory]
    [InlineData(new[] { SubmissionStatus.Rejected, SubmissionStatus.Approved }, SubmissionStatus.Approved)]
    [InlineData(new[] { SubmissionStatus.Rejected, SubmissionStatus.Pending }, SubmissionStatus.Pending)]
    [InlineData(new[] { SubmissionStatus.Received, SubmissionStatus.Rejected }, SubmissionStatus.Received)]
    [InlineData(new[] { SubmissionStatus.Approved, SubmissionStatus.Awesome, SubmissionStatus.Pending }, SubmissionStatus.Awesome)]
    public void Best_ShouldPreferAwesomeThenApprovedThenPendingThenRejected(SubmissionStatus[] statuses, SubmissionStatus expected)
    {
        statuses.Best().Should().Be(expected);
    }

    [Fact]
    public void Submission_TransitionStatus_ShouldRecordReview()
    {
        var submission = new Submission();

        var review = submission.TransitionStatus(SubmissionStatus.Rejected, "/r/a.jpg", "U1", "x", "dubbel");

        submission.Status.Should().Be(SubmissionStatus.Rejected);
        submission.StoragePath.Should().Be("/r/a.jpg");
        submission.UpdatedAt.Should().NotBeNull();
        review.SubmissionId.Should().Be(submission.Id);
        review.Submission.Should().BeNull();
        review.Notes.Should().Be("dubbel");
        submission.Reviews.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(4, 1, 1, 50)]
    [InlineData(3, 1, 0, 33.3)]
    public void GroupStatistic_ApprovalRate_ShouldIncludeAwesome(int total, int approved, int awesome, double expected)
    {
        new GroupStatistic { TotalSubmissions = total, ApprovedCount = approved, AwesomeCount = awesome }.ApprovalRate.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(4, 3, 75)]
    public void SubmissionStatistics_OverallApprovalRate(int total, int approved, double expected)
    {
        new SubmissionStatistics { TotalSubmissions = total, ApprovedCount = approved }.OverallApprovalRate.Should().Be(expected);
    }

    [Fact]
    public void AssignmentPointsCsvReader_EmptyInput_ShouldReturnNothing()
    {
        AssignmentPointsCsvReader.Read(new StringReader("")).Should().BeEmpty();
    }

    [Fact]
    public void SubmissionFilter_ShouldHoldAllCriteria()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var filter = new SubmissionFilter { FromDate = from, ToDate = from.AddDays(1), SearchTerm = "x", Reviewer = "y" };

        filter.FromDate.Should().Be(from);
        filter.ToDate.Should().Be(from.AddDays(1));
    }

    [Fact]
    public void SlackConnectionStatus_ShouldTrackSinceOnlyOnStateChange()
    {
        var status = new SlackConnectionStatus();
        status.Set(SlackConnectionState.Connecting);
        var since = status.Since;

        status.Set(SlackConnectionState.Connecting, "retry");

        status.Since.Should().Be(since);
        status.Detail.Should().Be("retry");
    }
}
