using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests;

public class WorkflowEndToEndIntegrationTests : IDisposable
{
    private readonly string _testStorageDir;
    private readonly ScoutingDbContext _dbContext;
    private readonly LocalFileStorageService _fileStorage;
    private readonly SubmissionRepository _submissionRepo;
    private readonly GroupRepository _groupRepo;
    private readonly AssignmentRepository _assignmentRepo;
    private readonly SubmissionWorkflowService _workflowService;

    public WorkflowEndToEndIntegrationTests()
    {
        _testStorageDir = Path.Combine(Path.GetTempPath(), $"ScoutingIntegration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testStorageDir);

        var dbOptions = new DbContextOptionsBuilder<ScoutingDbContext>()
            .UseInMemoryDatabase(databaseName: $"ScoutingDb_{Guid.NewGuid():N}")
            .Options;

        _dbContext = new ScoutingDbContext(dbOptions);
        _submissionRepo = new SubmissionRepository(_dbContext);
        _groupRepo = new GroupRepository(_dbContext);
        _assignmentRepo = new AssignmentRepository(_dbContext);

        var scoutingOptions = Options.Create(new ScoutingSettings
        {
            StorageRoot = _testStorageDir,
            SupportedExtensions = new List<string> { ".jpg", ".png", ".pdf", ".mp4" },
            Reviewers = new List<string> { "Sara_Jury" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup", "+1" },
                Rejected = new List<string> { "x", "-1" },
                Awesome = new List<string> { "heart", "star" }
            },
            Slack = new SlackSettings { SimulationMode = true }
        });

        _fileStorage = new LocalFileStorageService(scoutingOptions, NullLogger<LocalFileStorageService>.Instance);
        var assignmentParser = new AssignmentParser(NullLogger<AssignmentParser>.Instance);
        var slackClient = new SlackClient(new HttpClient(), scoutingOptions, NullLogger<SlackClient>.Instance);

        _workflowService = new SubmissionWorkflowService(
            _submissionRepo,
            _groupRepo,
            _assignmentRepo,
            assignmentParser,
            _fileStorage,
            slackClient,
            new SubmissionChangeNotifier(NullLogger<SubmissionChangeNotifier>.Instance),
            scoutingOptions,
            NullLogger<SubmissionWorkflowService>.Instance
        );
    }

    [Fact]
    public async Task CompleteWorkflow_FromSlackUploadToEmojiReviewAndReporting_ShouldSucceedEndToEnd()
    {
        // 1. Seed Group & Assignment
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "De Zeearenden",
            SlackChannelId = "C01TESTING"
        };
        await _groupRepo.UpsertAsync(group);

        var assignment = new Assignment
        {
            Number = 5,
            StandardPoints = 2,
            AwesomePoints = 4
        };
        await _assignmentRepo.UpsertAsync(assignment);

        // 2. Incoming Slack Message with File
        const string messageTs = "1720000000.1234";
        var messageEvent = new SlackEventItemDto
        {
            Type = "message",
            Channel = group.SlackChannelId,
            User = "Ruben_Scout",
            Text = "Opdracht 5: Hier is onze houten lepel!",
            Ts = messageTs,
            Files = new List<SlackFileItemDto>
            {
                new()
                {
                    Id = "F123",
                    Name = "lepel.jpg",
                    Size = 2048,
                    Mimetype = "image/jpeg",
                    UrlPrivateDownload = "sim://lepel.jpg"
                }
            }
        };

        var postResult = await _workflowService.ProcessSlackMessageAsync(messageEvent);

        // Assert Step 2
        postResult.Success.Should().BeTrue();
        postResult.CreatedSubmissionIds.Should().HaveCount(1);
        var subId = postResult.CreatedSubmissionIds[0];

        var createdSub = await _submissionRepo.GetByIdAsync(subId);
        createdSub.Should().NotBeNull();
        createdSub!.Status.Should().Be(SubmissionStatus.Pending);
        createdSub.AssignmentNumber.Should().Be(5);
        createdSub.FileName.Should().Be("lepel.jpg");
        File.Exists(createdSub.StoragePath).Should().BeTrue();
        Path.GetDirectoryName(createdSub.StoragePath).Should().Be(Path.Combine(_testStorageDir, "de-zeearenden"));

        // 3. Incoming Review Reaction: thumbsup (Approved)
        var reactionEvent = new SlackEventItemDto
        {
            Type = "reaction_added",
            Reaction = "thumbsup",
            User = "Sara_Jury",
            Item = new SlackReactionTargetDto
            {
                Channel = group.SlackChannelId,
                Ts = messageTs
            }
        };

        var reactionResult = await _workflowService.ProcessSlackReactionAsync(reactionEvent);

        // Assert Step 3
        reactionResult.Success.Should().BeTrue();
        reactionResult.NewStatus.Should().Be(SubmissionStatus.Approved);

        var reviewedSub = await _submissionRepo.GetByIdAsync(subId);
        reviewedSub!.Status.Should().Be(SubmissionStatus.Approved);
        File.Exists(reviewedSub.StoragePath).Should().BeTrue();
        reviewedSub.StoragePath.Should().Contain(Path.Combine("de-zeearenden", "approved"));
        reviewedSub.Reviews.Should().ContainSingle(r => r.Reviewer == "Sara_Jury" && r.Status == SubmissionStatus.Approved);

        // 4. Verify Dashboard Statistics Reporting (FR-009)
        var stats = await _submissionRepo.GetStatisticsAsync();
        stats.TotalSubmissions.Should().Be(1);
        stats.ApprovedCount.Should().Be(1);
        stats.PendingCount.Should().Be(0);
        stats.OverallApprovalRate.Should().Be(100.0);

        var groupStat = stats.GroupStatistics.FirstOrDefault(g => g.GroupId == group.Id);
        groupStat.Should().NotBeNull();
        groupStat!.ApprovedCount.Should().Be(1);
        groupStat.ApprovalRate.Should().Be(100.0);
        groupStat.Score.Should().Be(2);

        var assignStat = stats.AssignmentStatistics.FirstOrDefault(a => a.AssignmentNumber == 5);
        assignStat.Should().NotBeNull();
        assignStat!.ApprovedCount.Should().Be(1);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            if (Directory.Exists(_testStorageDir))
            {
                Directory.Delete(_testStorageDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors
        }
    }
}
