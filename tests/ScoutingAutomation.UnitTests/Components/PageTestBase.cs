using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Extensions;
using ScoutingAutomation.Infrastructure.Services;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>
/// Renders pages against the real infrastructure with an in-memory database.
/// </summary>
public abstract class PageTestBase : BunitContext
{
    private readonly TempDirectory _temp = new();

    protected PageTestBase()
    {
        var databaseName = $"Pages_{Guid.NewGuid():N}";
        var databaseRoot = new InMemoryDatabaseRoot();

        Services.AddDbContext<ScoutingDbContext>(o => o.UseInMemoryDatabase(databaseName, databaseRoot));
        Services.AddScoutingInfrastructure(new ConfigurationBuilder().Build());
        Services.AddLogging();
        Services.AddScoped<ScoutingAutomation.Web.Favorites.FavoritesService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Auth = AddAuthorization();
        Auth.SetAuthorized("Admin");
        Auth.SetRoles(ScoutingAutomation.Web.Auth.KeyAuth.AdminRole);
        // Resolved lazily so tests can adjust Settings before rendering
        Services.AddSingleton<IOptions<ScoutingSettings>>(_ => Options.Create(Settings));

        Settings = new ScoutingSettings
        {
            StorageRoot = _temp.Path,
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
            KeysFile = Path.Combine(_temp.Path, "access-keys.json"),
            SupportedExtensions = new List<string> { ".jpg", ".png", ".pdf" },
            SupportedMimeTypePrefixes = new List<string> { "image/", "video/", "audio/" },
            Reviewers = new List<string> { "U_JURY" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup" },
                Rejected = new List<string> { "x" },
                Awesome = new List<string> { "heart" }
            },
            Slack = new SlackSettings { SimulationMode = true }
        };
    }

    protected ScoutingSettings Settings { get; }

    /// <summary>Tests run as an admin; call <see cref="LogInAsJury"/> to check what others see.</summary>
    protected Bunit.TestDoubles.BunitAuthorizationContext Auth { get; }

    protected void LogInAsJury()
    {
        Auth.SetAuthorized("Jury");
        Auth.SetRoles();
    }

    protected string StorageRoot => _temp.Path;

    protected Group Wolves { get; } = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1", Description = "Welpen horde" };
    protected Group Owls { get; } = new() { Name = "De Bosuilen", Abbreviation = "DBU", SlackChannelId = "C2" };
    protected Group Foxes { get; } = new() { Name = "De Vossen", SlackChannelId = "C3" };

    protected T WithDb<T>(Func<ScoutingDbContext, T> action)
    {
        using var scope = Services.CreateScope();
        return action(scope.ServiceProvider.GetRequiredService<ScoutingDbContext>());
    }

    protected void WithDb(Action<ScoutingDbContext> action) => WithDb(db => { action(db); return 0; });

    /// <summary>
    /// Wolves: #1 rejected then approved, #2 awesome. Owls: #1 pending, one unresolved upload. Foxes: nothing.
    /// </summary>
    protected Seeded Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var rejected = NewSubmission(Wolves, 1, SubmissionStatus.Rejected, "vuur1.jpg", now.AddHours(-3), "image/jpeg", 500);
        rejected.Reviews.Add(new Review { Reviewer = "U_JURY", Status = SubmissionStatus.Rejected, ReactionEmoji = "x", Notes = "Te donker", CreatedAt = now.AddHours(-2) });
        var approved = NewSubmission(Wolves, 1, SubmissionStatus.Approved, "vuur2.jpg", now.AddHours(-1), "image/jpeg", 5_000);
        approved.Reviews.Add(new Review { Reviewer = "U_JURY", Status = SubmissionStatus.Approved, ReactionEmoji = "thumbsup", CreatedAt = now });
        var awesome = NewSubmission(Wolves, 2, SubmissionStatus.Awesome, "toren.mp4", now.AddHours(-2), "video/mp4", 5_000_000);
        var pending = NewSubmission(Owls, 1, SubmissionStatus.Pending, "knopen.pdf", now.AddMinutes(-30), "application/pdf", 2_048);
        var unresolved = NewSubmission(Owls, null, SubmissionStatus.Pending, "raadsel.m4a", now.AddMinutes(-10), "audio/mp4", 100);
        unresolved.UnresolvedReason = "No valid assignment number pattern found in: 'hoi'";

        WithDb(db =>
        {
            db.Groups.AddRange(Wolves, Owls, Foxes);
            db.Assignments.AddRange(
                new Assignment { Number = 1, StandardPoints = 1, AwesomePoints = 2 },
                new Assignment { Number = 2, StandardPoints = 2, AwesomePoints = 4 },
                new Assignment { Number = 3, StandardPoints = 1, AwesomePoints = 2 });
            db.Submissions.AddRange(rejected, approved, awesome, pending, unresolved);
            db.SaveChanges();
        });

        return new Seeded(rejected, approved, awesome, pending, unresolved);
    }

    protected Submission NewSubmission(Group group, int? number, SubmissionStatus status, string fileName, DateTimeOffset createdAt, string contentType, long size)
    {
        var folder = Path.Combine(StorageRoot, group.Name.ToLowerInvariant().Replace(' ', '-'));
        Directory.CreateDirectory(folder);
        var stored = Path.Combine(folder, number.HasValue ? $"{group.Abbreviation ?? group.Name} {number}{Path.GetExtension(fileName)}" : fileName);
        File.WriteAllBytes(stored, new byte[] { 1 });

        return new Submission
        {
            GroupId = group.Id,
            AssignmentNumber = number,
            Status = status,
            FileName = fileName,
            StoragePath = stored,
            ContentType = contentType,
            FileSizeBytes = size,
            Submitter = "U_SCOUT",
            SlackChannelId = group.SlackChannelId,
            SlackMessageId = $"{createdAt.ToUnixTimeMilliseconds()}.{Guid.NewGuid():N}",
            MessageText = number.HasValue ? $"Opdracht {number}" : "hoi",
            CreatedAt = createdAt
        };
    }

    protected void NotifyChanged() => Services.GetRequiredService<ISubmissionChangeNotifier>().NotifyChanged();

    protected string CurrentUri => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _temp.Dispose();
        }
    }

    protected sealed record Seeded(Submission Rejected, Submission Approved, Submission Awesome, Submission Pending, Submission Unresolved);
}
