using FluentAssertions;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using Xunit;

namespace ScoutingAutomation.UnitTests;

public class StatusTransitionTests
{
    [Fact]
    public void Submission_InitialState_ShouldBeReceived()
    {
        var submission = new Submission();
        submission.Status.Should().Be(SubmissionStatus.Received);
        submission.Reviews.Should().BeEmpty();
    }

    [Fact]
    public void MarkPending_ShouldUpdateStatusAndStoragePath()
    {
        var submission = new Submission
        {
            FileName = "vuur.jpg"
        };

        submission.MarkPending("/storage/group/vuur.jpg", 1);

        submission.Status.Should().Be(SubmissionStatus.Pending);
        submission.StoragePath.Should().Be("/storage/group/vuur.jpg");
        submission.AssignmentNumber.Should().Be(1);
        submission.UnresolvedReason.Should().BeNull();
        submission.UpdatedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(SubmissionStatus.Approved, "thumbsup", "Sara_Jury")]
    [InlineData(SubmissionStatus.Awesome, "heart", "Mark_Jury")]
    [InlineData(SubmissionStatus.Rejected, "x", "Alex_Jury")]
    public void TransitionStatus_ShouldRecordReviewAndChangeStatus(SubmissionStatus newStatus, string reactionEmoji, string reviewer)
    {
        var submission = new Submission
        {
            FileName = "vlaggenmast.png"
        };
        submission.MarkPending("/storage/group/vlaggenmast.png", 2);

        var newPath = $"/storage/group/{newStatus.ToString().ToLower()}/vlaggenmast.png";
        var review = submission.TransitionStatus(newStatus, newPath, reviewer, reactionEmoji, "Looks great!");

        submission.Status.Should().Be(newStatus);
        submission.StoragePath.Should().Be(newPath);
        submission.Reviews.Should().ContainSingle();

        review.Status.Should().Be(newStatus);
        review.Reviewer.Should().Be(reviewer);
        review.ReactionEmoji.Should().Be(reactionEmoji);
        review.Notes.Should().Be("Looks great!");
    }
}
