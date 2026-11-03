using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Editions;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Storage;
using Xunit;

namespace ScoutingAutomation.UnitTests.Editions;

public class EditionArchiveTests : IDisposable
{
    private static readonly byte[] Photo = [1, 2, 3, 4, 5];

    private readonly TempDirectory _temp = new();
    private readonly string _here;
    private readonly ScoutingDbContext _db = TestDb.Create();
    private readonly Edition _edition;
    private readonly Group _wolves;
    private readonly Submission _submission;

    public EditionArchiveTests()
    {
        _here = Path.Combine(_temp.Path, "here");
        _edition = new Edition
        {
            Name = "DU 2025", Year = 2025, Status = EditionStatus.Archived, ArchivedAt = new DateTimeOffset(2025, 10, 5, 18, 0, 0, TimeSpan.Zero)
        };
        _db.Editions.Add(_edition);
        _db.Editions.Add(new Edition { Name = "DU 2026", Year = 2026, Status = EditionStatus.Active });
        _wolves = new Group { EditionId = _edition.Id, Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1", MapToken = "kaart" };
        _db.Groups.Add(_wolves);
        _db.Assignments.Add(new Assignment { EditionId = _edition.Id, Code = "5", Title = "Lepel", StandardPoints = 2, AwesomePoints = 4 });
        _db.Locations.Add(new Location { EditionId = _edition.Id, Number = 7, Name = "Molen", Latitude = 51.6, Longitude = 5 });
        _db.SaveChanges();

        var file = Path.Combine(_here, "de-wolven", "approved", "DWO 5 10-00-00.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Photo);
        _submission = new Submission
        {
            GroupId = _wolves.Id, AssignmentCode = "5", SlackChannelId = "C1", SlackMessageId = "1.1", SlackFileId = "F1", Submitter = "U1",
            SubmitterName = "Eva", FileName = "lepel.jpg", StoragePath = file, FileSizeBytes = Photo.Length, ContentType = "image/jpeg",
            Status = SubmissionStatus.Approved, MessageText = "Opdracht 5", CreatedAt = new DateTimeOffset(2025, 10, 4, 10, 0, 0, TimeSpan.Zero)
        };
        _submission.Reviews.Add(new Review { Id = Guid.NewGuid(), SubmissionId = _submission.Id, Reviewer = "U_JURY", Status = SubmissionStatus.Approved, ReactionEmoji = "thumbsup" });
        _db.Submissions.Add(_submission);
        _db.GroupBonusPoints.Add(new GroupBonusPoints { GroupId = _wolves.Id, CategoryKey = "sport", Points = 3 });
        _db.BonusPointsEntries.Add(new BonusPointsEntry { GroupId = _wolves.Id, CategoryKey = "sport", Delta = 3, Total = 3, ChangedAt = DateTimeOffset.UtcNow });
        _db.LocationUnlocks.Add(new LocationUnlock { GroupId = _wolves.Id, LocationNumber = 7, UnlockedAt = DateTimeOffset.UtcNow });
        _edition.FinalStandingJson = JsonSerializer.Serialize(new[] { new EditionStanding(_wolves.TeamId, _wolves.Id, "De Wolven", 1, 5) });
        _db.SaveChanges();
    }

    private EditionArchive Archive(ScoutingDbContext db, string storageRoot)
    {
        var settings = Options.Create(new ScoutingSettings { StorageRoot = storageRoot });
        return new EditionArchive(db, new SubmissionRepository(db), new LocalFileStorageService(settings, NullLogger<LocalFileStorageService>.Instance),
            settings, TimeProvider.System, NullLogger<EditionArchive>.Instance);
    }

    private async Task<string> ExportAsync()
    {
        var zip = Path.Combine(_temp.Path, $"{Guid.NewGuid():N}.zip");
        await using var file = File.Create(zip);
        (await Archive(_db, _here).ExportAsync(_edition.Id, file)).Should().BeEmpty();
        return zip;
    }

    [Fact]
    public async Task ExportThenImportElsewhere_ShouldBringEverythingAlongAsAnArchivedEdition()
    {
        var zip = await ExportAsync();
        var thereRoot = Path.Combine(_temp.Path, "there");
        await using var there = TestDb.Create();
        there.Editions.Add(new Edition { Name = "DU 2027", Year = 2027, Status = EditionStatus.Active });
        await there.SaveChangesAsync();

        var (edition, error) = await Archive(there, thereRoot).ImportAsync(zip);

        error.Should().BeNull();
        edition!.Should().BeEquivalentTo(new { Name = "DU 2025", Year = 2025, Status = EditionStatus.Archived, StorageFolder = "_edities/du-2025" });
        edition.ArchivedAt.Should().Be(_edition.ArchivedAt);
        var group = await there.Groups.IgnoreQueryFilters().SingleAsync(g => g.EditionId == edition.Id);
        group.Should().BeEquivalentTo(new { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1", TeamId = _wolves.TeamId });
        (await there.Teams.SingleAsync()).Name.Should().Be("De Wolven");
        (await there.Assignments.IgnoreQueryFilters().SingleAsync(a => a.EditionId == edition.Id)).Title.Should().Be("Lepel");
        (await there.Locations.IgnoreQueryFilters().SingleAsync(l => l.EditionId == edition.Id)).Name.Should().Be("Molen");

        var submission = await there.Submissions.IgnoreQueryFilters().Include(s => s.Reviews).SingleAsync();
        submission.Should().BeEquivalentTo(new { GroupId = group.Id, AssignmentCode = "5", SubmitterName = "Eva", Status = SubmissionStatus.Approved, CreatedAt = _submission.CreatedAt });
        submission.Reviews.Should().ContainSingle().Which.ReactionEmoji.Should().Be("thumbsup");
        submission.StoragePath.Should().StartWith(Path.Combine(thereRoot, "_edities", "du-2025", "de-wolven", "approved"));
        File.ReadAllBytes(submission.StoragePath).Should().Equal(Photo);

        (await there.GroupBonusPoints.IgnoreQueryFilters().SingleAsync()).Should().BeEquivalentTo(new { GroupId = group.Id, Points = 3 });
        (await there.BonusPointsEntries.IgnoreQueryFilters().SingleAsync()).GroupId.Should().Be(group.Id);
        (await there.LocationUnlocks.IgnoreQueryFilters().SingleAsync()).GroupId.Should().Be(group.Id);
        EditionService.ReadStanding(edition).Should().ContainSingle().Which.Should().BeEquivalentTo(new { GroupId = group.Id, Rank = 1, Score = 5 });
        (await there.Groups.CountAsync()).Should().Be(0, "the edition being played does not change");
    }

    [Fact]
    public async Task ImportIntoTheSameApp_ShouldUseTheTeamsThatAreThere()
    {
        var zip = await ExportAsync();
        _edition.Name = "DU 2025 oud";
        await _db.SaveChangesAsync();

        var (edition, error) = await Archive(_db, _here).ImportAsync(zip);

        error.Should().BeNull();
        (await _db.Teams.CountAsync()).Should().Be(1);
        (await _db.Groups.IgnoreQueryFilters().SingleAsync(g => g.EditionId == edition!.Id)).TeamId.Should().Be(_wolves.TeamId);
        (await _db.Groups.IgnoreQueryFilters().SingleAsync(g => g.EditionId == edition!.Id)).Id.Should().NotBe(_wolves.Id);
    }

    [Fact]
    public async Task Import_OfAnEditionThatIsThere_ShouldBeRefused()
    {
        var zip = await ExportAsync();

        var (edition, error) = await Archive(_db, _here).ImportAsync(zip);

        edition.Should().BeNull();
        error.Should().Contain("al een editie DU 2025");
        (await _db.Editions.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData("C1", "C2", "C3", "C1", false, "Slack-kanaal")]
    [InlineData("C1", "C2", "C2", null, false, "Slack-kanaal")]
    [InlineData("C1", null, "C2", null, true, "team")]
    public async Task Import_WithGroupsThatWouldClash_ShouldBeRefused(string first, string? firstAdventurers, string second, string? secondAdventurers, bool sameTeam, string problem)
    {
        var team = Guid.NewGuid();
        var data = new
        {
            format = EditionArchive.Format, version = 1, exportedAt = DateTimeOffset.UtcNow,
            edition = new { id = Guid.NewGuid(), name = "Botsing", year = 2025, status = "Draft", createdAt = DateTimeOffset.UtcNow },
            groups = new[]
            {
                new { id = Guid.NewGuid(), teamId = team, name = "A", slackChannelId = first, adventurersSlackChannelId = firstAdventurers, createdAt = DateTimeOffset.UtcNow },
                new { id = Guid.NewGuid(), teamId = sameTeam ? team : Guid.NewGuid(), name = "B", slackChannelId = second, adventurersSlackChannelId = secondAdventurers, createdAt = DateTimeOffset.UtcNow }
            }
        };
        var zip = Path.Combine(_temp.Path, "botsing.zip");
        await using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        await using (var writer = new StreamWriter(archive.CreateEntry(EditionArchive.DataEntry).Open()))
        {
            await writer.WriteAsync(JsonSerializer.Serialize(data));
        }

        var (edition, error) = await Archive(_db, _here).ImportAsync(zip);

        edition.Should().BeNull();
        error.Should().Contain(problem);
        (await _db.Editions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Import_WithAPathOutsideTheEdition_ShouldWriteNothing()
    {
        var data = new
        {
            format = EditionArchive.Format, version = 1, exportedAt = DateTimeOffset.UtcNow,
            edition = new { id = Guid.NewGuid(), name = "Kwaad", year = 2025, status = "Archived", createdAt = DateTimeOffset.UtcNow },
            groups = new[] { new { id = _wolves.Id, teamId = Guid.NewGuid(), name = "X", slackChannelId = "C9", createdAt = DateTimeOffset.UtcNow } },
            submissions = new[]
            {
                new
                {
                    id = Guid.NewGuid(), groupId = _wolves.Id, kind = "Assignment", slackChannelId = "C9", slackMessageId = "1", submitter = "U",
                    fileName = "x.txt", file = "files/../../../buiten.txt", contentType = "text/plain", status = "Approved", createdAt = DateTimeOffset.UtcNow
                }
            }
        };
        var zip = Path.Combine(_temp.Path, "kwaad.zip");
        await using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await using (var writer = new StreamWriter(archive.CreateEntry(EditionArchive.DataEntry).Open()))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(data));
            }

            await using var file = archive.CreateEntry("files/../../../buiten.txt").Open();
            await file.WriteAsync(Encoding.UTF8.GetBytes("buiten"));
        }

        var (edition, error) = await Archive(_db, _here).ImportAsync(zip);

        edition.Should().BeNull();
        error.Should().Contain("buiten de map");
        (await _db.Editions.CountAsync()).Should().Be(2);
        Directory.EnumerateFiles(_temp.Path, "buiten.txt", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.Exists(Path.Combine(_here, "_edities", "kwaad")).Should().BeFalse("a failed import leaves no folder behind");
    }

    [Fact]
    public async Task Import_OfSomethingElse_ShouldSayWhat()
    {
        var notZip = Path.Combine(_temp.Path, "foto.zip");
        await File.WriteAllBytesAsync(notZip, Photo);
        (await Archive(_db, _here).ImportAsync(notZip)).Error.Should().Contain("geen ZIP");

        var otherZip = Path.Combine(_temp.Path, "ander.zip");
        await using (var stream = File.Create(otherZip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            archive.CreateEntry("foto.jpg");
        }

        (await Archive(_db, _here).ImportAsync(otherZip)).Error.Should().Contain(EditionArchive.DataEntry);

        var newer = Path.Combine(_temp.Path, "nieuwer.zip");
        await using (var stream = File.Create(newer))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await using var writer = new StreamWriter(archive.CreateEntry(EditionArchive.DataEntry).Open());
            await writer.WriteAsync(JsonSerializer.Serialize(new { format = EditionArchive.Format, version = 99, edition = new { name = "X" } }));
        }

        (await Archive(_db, _here).ImportAsync(newer)).Error.Should().Contain("nieuwere versie");
    }

    [Fact]
    public async Task Export_OfAMissingFile_ShouldListItAndKeepGoing()
    {
        File.Delete(_submission.StoragePath);
        var zip = Path.Combine(_temp.Path, "zonder.zip");

        await using (var file = File.Create(zip))
        {
            (await Archive(_db, _here).ExportAsync(_edition.Id, file)).Should().ContainSingle();
        }

        await using var read = await ZipFile.OpenReadAsync(zip);
        read.Entries.Select(e => e.FullName).Should().Equal(EditionArchive.DataEntry);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }
}
