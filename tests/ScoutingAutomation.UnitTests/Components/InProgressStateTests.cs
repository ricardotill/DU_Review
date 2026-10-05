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
using ScoutingAutomation.Infrastructure.Workflow;
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
        new object[] { typeof(AssignmentDetail), "code:1" },
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
            string s when s.StartsWith("code:") => s,
            _ => parameter
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent(0, page);
            if (value is string code) builder.AddAttribute(1, "Code", code["code:".Length..]);
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
        cut.Find("button.btn-scout-primary").TextContent.Should().Contain("Verwerken…");
        uploadGate.SetResult();
        cut.WaitForAssertion(() => cut.Find("button.btn-scout-primary").TextContent.Should().Contain("Upload plaatsen"));

        var reactionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowHold = reactionGate.Task;
        cut.FindAll("select")[1].Change(seeded.Pending.Id.ToString());
        var reactButton = () => cut.FindAll("button").Single(b => b.TextContent.Contains("Emoji :") || b.TextContent.Contains("Verwerken…") && b.ClassList.Contains("btn-scout-secondary"));
        reactButton().Click();
        // The handler first loads the submission, which may finish on another thread
        cut.WaitForAssertion(() => reactButton().TextContent.Should().Contain("Verwerken…"));
        reactionGate.SetResult();
        cut.WaitForAssertion(() => reactButton().TextContent.Should().Contain("Emoji :"));
    }

    private sealed class GatedSubmissionRepository(ISubmissionRepository inner, Task gate) : ISubmissionRepository
    {
        public async Task<Submission?> GetByIdAsync(Guid id, CancellationToken ct = default) { await gate; return await inner.GetByIdAsync(id, ct); }
        public Task<IReadOnlyList<Submission>> GetBySlackMessageIdAsync(string c, string m, CancellationToken ct = default) => inner.GetBySlackMessageIdAsync(c, m, ct);
        public Task DeleteAsync(IReadOnlyList<Submission> submissions, CancellationToken ct = default) => inner.DeleteAsync(submissions, ct);
        public Task DeleteAllAsync(CancellationToken ct = default) => inner.DeleteAllAsync(ct);
        public Task DeleteByGroupAsync(Guid groupId, CancellationToken ct = default) => inner.DeleteByGroupAsync(groupId, ct);
        public async Task<IReadOnlyList<Submission>> SearchSubmissionsAsync(SubmissionFilter filter, CancellationToken ct = default) { await gate; return await inner.SearchSubmissionsAsync(filter, ct); }
        public async Task<SubmissionStatistics> GetStatisticsAsync(CancellationToken ct = default) { await gate; return await inner.GetStatisticsAsync(ct); }
        public Task AddAsync(Submission submission, CancellationToken ct = default) => inner.AddAsync(submission, ct);
        public Task UpdateAsync(Submission submission, CancellationToken ct = default) => inner.UpdateAsync(submission, ct);
        public Task<bool> ExistsAsync(string c, string m, string? id, string f, CancellationToken ct = default) => inner.ExistsAsync(c, m, id, f, ct);
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

        public Task<ProcessSlackMessageResult> ProcessSlackMessageChangedAsync(SlackEventItemDto e, CancellationToken ct = default) =>
            inner.ProcessSlackMessageChangedAsync(e, ct);

        public Task<ProcessSlackMessageResult> ProcessSlackMessageDeletedAsync(SlackEventItemDto e, CancellationToken ct = default) =>
            inner.ProcessSlackMessageDeletedAsync(e, ct);

        public Task<ProcessSlackMessageResult> ProcessMissedMessageAsync(SlackEventItemDto message, CancellationToken ct = default) =>
            inner.ProcessMissedMessageAsync(message, ct);

        public Task<bool> SyncReviewAsync(string channelId, string messageTs, IReadOnlyList<SlackMessageReactionDto>? reactions, CancellationToken ct = default) =>
            inner.SyncReviewAsync(channelId, messageTs, reactions, ct);

        public Task<Submission> OverrideStatusAsync(Guid id, SubmissionStatus status, string reason, CancellationToken ct = default) =>
            inner.OverrideStatusAsync(id, status, reason, ct);

        public Task<ProcessSlackMessageResult> ImportSlackMessageAsync(SlackEventItemDto message, CancellationToken ct = default) =>
            inner.ImportSlackMessageAsync(message, ct);

        public Task<ProcessSlackReactionResult> ProcessSlackReactionRemovedAsync(SlackEventItemDto e, CancellationToken ct = default) =>
            inner.ProcessSlackReactionRemovedAsync(e, ct);

        public Task<ImportedReviewResult> ApplyImportedReactionsAsync(string channelId, string messageTs, IReadOnlyList<SlackMessageReactionDto>? reactions, CancellationToken ct = default) =>
            inner.ApplyImportedReactionsAsync(channelId, messageTs, reactions, ct);
    }
}
