using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>
/// What pages show while data is loading or a Slack action is being processed.
/// A gate holds the database or workflow call open until the test releases it.
/// </summary>
public class InProgressStateTests : PageTestBase
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _workflowHold;

    public InProgressStateTests()
    {
        Services.AddScoped<SubmissionRepository>();
        Services.RemoveAll<ISubmissionRepository>();
        Services.AddScoped<ISubmissionRepository>(sp => new GatedSubmissionRepository(sp.GetRequiredService<SubmissionRepository>(), _gate.Task));

        Services.AddScoped<SubmissionWorkflowService>();
        Services.RemoveAll<ISubmissionWorkflowService>();
        Services.AddScoped<ISubmissionWorkflowService>(sp => new GatedWorkflow(sp.GetRequiredService<SubmissionWorkflowService>(), () => _workflowHold));
    }

    public static IEnumerable<object[]> LoadingPages() => new[]
    {
        new object[] { typeof(Home), null! },
        new object[] { typeof(Groups), null! },
        new object[] { typeof(Assignments), null! },
        new object[] { typeof(AssignmentDetail), 1 },
        new object[] { typeof(GroupDetail), "wolves" },
        new object[] { typeof(SubmissionDetail), "submission" }
    };

    [Theory]
    [MemberData(nameof(LoadingPages))]
    public void Page_ShouldShowSpinnerUntilDataArrives(Type page, object? parameter)
    {
        var seeded = Seed();
        object? value = parameter switch
        {
            "wolves" => Wolves.Id,
            "submission" => seeded.Approved.Id,
            _ => parameter
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent(0, page);
            if (value is int number) builder.AddAttribute(1, "Number", number);
            if (value is Guid id) builder.AddAttribute(1, "Id", id);
            builder.CloseComponent();
        });

        cut.FindAll(".spinner-border").Should().NotBeEmpty();
        _gate.SetResult();
        cut.WaitForAssertion(() => cut.FindAll(".spinner-border").Should().BeEmpty());
    }

    [Fact]
    public void Simulator_ShouldShowProcessingWhileUploadAndReactionRun()
    {
        var seeded = Seed();
        _gate.SetResult(); // let the simulator load its lists
        var cut = Render<SlackSimulator>();
        cut.WaitForAssertion(() => cut.FindAll("select")[1].QuerySelectorAll("option").Length.Should().BeGreaterThan(1));

        var uploadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowHold = uploadGate.Task;
        cut.Find("button.btn-scout-primary").Click();
        cut.Find("button.btn-scout-primary").TextContent.Should().Contain("Processing…");
        uploadGate.SetResult();
        cut.WaitForAssertion(() => cut.Find("button.btn-scout-primary").TextContent.Should().Contain("Post upload"));

        var reactionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowHold = reactionGate.Task;
        cut.FindAll("select")[1].Change(seeded.Pending.Id.ToString());
        var reactButton = () => cut.FindAll("button").Single(b => b.TextContent.Contains("Add reaction") || b.TextContent.Contains("Processing…") && b.ClassList.Contains("btn-scout-secondary"));
        reactButton().Click();
        reactButton().TextContent.Should().Contain("Processing…");
        reactionGate.SetResult();
        cut.WaitForAssertion(() => reactButton().TextContent.Should().Contain("Add reaction"));
    }

    private sealed class GatedSubmissionRepository(ISubmissionRepository inner, Task gate) : ISubmissionRepository
    {
        public async Task<Submission?> GetByIdAsync(Guid id, CancellationToken ct = default) { await gate; return await inner.GetByIdAsync(id, ct); }
        public Task<IReadOnlyList<Submission>> GetBySlackMessageIdAsync(string c, string m, CancellationToken ct = default) => inner.GetBySlackMessageIdAsync(c, m, ct);
        public Task DeleteAllAsync(CancellationToken ct = default) => inner.DeleteAllAsync(ct);
        public async Task<IReadOnlyList<Submission>> SearchSubmissionsAsync(SubmissionFilter filter, CancellationToken ct = default) { await gate; return await inner.SearchSubmissionsAsync(filter, ct); }
        public async Task<SubmissionStatistics> GetStatisticsAsync(CancellationToken ct = default) { await gate; return await inner.GetStatisticsAsync(ct); }
        public Task AddAsync(Submission submission, CancellationToken ct = default) => inner.AddAsync(submission, ct);
        public Task UpdateAsync(Submission submission, CancellationToken ct = default) => inner.UpdateAsync(submission, ct);
        public Task<bool> ExistsAsync(string c, string m, string f, CancellationToken ct = default) => inner.ExistsAsync(c, m, f, ct);
    }

    private sealed class GatedWorkflow(ISubmissionWorkflowService inner, Func<Task?> hold) : ISubmissionWorkflowService
    {
        public async Task<ProcessSlackMessageResult> ProcessSlackMessageAsync(SlackEventItemDto e, CancellationToken ct = default)
        {
            if (hold() is { } gate) await gate;
            return await inner.ProcessSlackMessageAsync(e, ct);
        }

        public async Task<ProcessSlackReactionResult> ProcessSlackReactionAsync(SlackEventItemDto e, CancellationToken ct = default)
        {
            if (hold() is { } gate) await gate;
            return await inner.ProcessSlackReactionAsync(e, ct);
        }

        public Task<Submission> OverrideStatusAsync(Guid id, SubmissionStatus status, string reason, CancellationToken ct = default) =>
            inner.OverrideStatusAsync(id, status, reason, ct);

        public Task<ProcessSlackMessageResult> ImportSlackMessageAsync(SlackEventItemDto message, CancellationToken ct = default) =>
            inner.ImportSlackMessageAsync(message, ct);

        public Task<ImportedReviewResult> ApplyImportedReactionsAsync(string channelId, string messageTs, IReadOnlyList<SlackMessageReactionDto>? reactions, CancellationToken ct = default) =>
            inner.ApplyImportedReactionsAsync(channelId, messageTs, reactions, ct);
    }
}
