using ScoutingAutomation.Infrastructure.Cluster;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Storage;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Editions;

public sealed record EditionSummary(Edition Edition, int GroupCount, int SubmissionCount);

/// <summary>A group as an admin enters it. No <see cref="Id"/>: a new group; no <see cref="TeamId"/>: a new team.</summary>
public sealed record GroupInput(
    Guid? Id,
    Guid? TeamId,
    string? Name,
    string? Abbreviation,
    string? SlackChannelId,
    string? AdventurersSlackChannelId,
    string? Description);

/// <summary>An edition a team played, with its place in the final standing (null when the team had no place).</summary>
public sealed record TeamEdition(Edition Edition, Group Group, EditionStanding? Standing);

/// <summary>
/// The editions: making a new one (with the assignments and, if wanted, the groups of the current one), its groups,
/// and switching the game to it. Only the active edition handles Slack; an archived one is no longer changed.
/// </summary>
public sealed class EditionService
{
    public const string EditionsFolderName = "_edities";

    private readonly ScoutingDbContext _db;
    private readonly AccountsDbContext _accounts;
    private readonly ActiveEdition _active;
    private readonly SlackProcessingGate _gate;
    private readonly IFileStorageService _storage;
    private readonly ISubmissionRepository _submissions;
    private readonly ISubmissionChangeNotifier _changes;
    private readonly AccountChangeNotifier _accountChanges;
    private readonly TimeProvider _time;
    private readonly ILogger<EditionService> _logger;

    public EditionService(
        ScoutingDbContext db,
        AccountsDbContext accounts,
        ActiveEdition active,
        SlackProcessingGate gate,
        IFileStorageService storage,
        ISubmissionRepository submissions,
        ISubmissionChangeNotifier changes,
        AccountChangeNotifier accountChanges,
        TimeProvider time,
        ILogger<EditionService> logger,
        IClusterLocks? cluster = null,
        IClusterBus? bus = null)
    {
        _cluster = cluster ?? SingleInstance.Instance;
        _bus = bus ?? SingleInstance.Instance;
        _db = db;
        _accounts = accounts;
        _active = active;
        _gate = gate;
        _storage = storage;
        _submissions = submissions;
        _changes = changes;
        _accountChanges = accountChanges;
        _time = time;
        _logger = logger;
    }

    private readonly IClusterLocks _cluster;
    private readonly IClusterBus _bus;

    private IQueryable<Group> AllGroups => _db.Groups.IgnoreQueryFilters();

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<EditionSummary>> GetAllAsync(CancellationToken ct = default)
    {
        var editions = await _db.Editions.AsNoTracking().ToListAsync(ct);
        var groups = await AllGroups.AsNoTracking().GroupBy(g => g.EditionId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var submissions = await _db.Submissions.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(s => s.Group!.EditionId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return editions
            .OrderByDescending(e => e.Year).ThenByDescending(e => e.CreatedAt)
            .Select(e => new EditionSummary(e,
                groups.FirstOrDefault(g => g.Key == e.Id)?.Count ?? 0,
                submissions.FirstOrDefault(s => s.Key == e.Id)?.Count ?? 0))
            .ToList();
    }

    public async Task<IReadOnlyList<Group>> GetGroupsAsync(Guid editionId, CancellationToken ct = default) =>
        await AllGroups.AsNoTracking().Include(g => g.Team).Where(g => g.EditionId == editionId).OrderBy(g => g.Name).ToListAsync(ct);

    /// <summary>Every team, also those that did not play the active edition; by name.</summary>
    public async Task<IReadOnlyList<Team>> GetTeamsAsync(CancellationToken ct = default) =>
        await _db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    public static IReadOnlyList<EditionStanding> ReadStanding(Edition edition)
    {
        if (string.IsNullOrWhiteSpace(edition.FinalStandingJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<EditionStanding>>(edition.FinalStandingJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The archived editions a team played, newest first; for the group's own history.</summary>
    public async Task<IReadOnlyList<TeamEdition>> GetTeamHistoryAsync(Guid teamId, CancellationToken ct = default)
    {
        var groups = await AllGroups.AsNoTracking().Include(g => g.Edition)
            .Where(g => g.TeamId == teamId && g.Edition!.Status == EditionStatus.Archived)
            .ToListAsync(ct);
        return groups
            .OrderByDescending(g => g.Edition!.Year).ThenByDescending(g => g.Edition!.CreatedAt)
            .Select(g => new TeamEdition(g.Edition!, g, ReadStanding(g.Edition!).FirstOrDefault(s => s.GroupId == g.Id)))
            .ToList();
    }

    // One change to the editions at a time, in the whole app and across its instances: two admins starting and deleting
    // the same draft, or adding the same channel twice, would otherwise both pass the checks
    private static readonly SemaphoreSlim Changes = new(1, 1);

    internal static Task<T> OneAtATimeAsync<T>(IClusterLocks cluster, Func<Task<T>> change, CancellationToken ct) =>
        cluster.OneAtATimeAsync(Changes, ClusterLockNames.Editions, change, ct);

    /// <inheritdoc cref="CreateCoreAsync"/>
    public Task<(Edition? Edition, string? Error)> CreateAsync(string? name, int year, bool copyGroups, CancellationToken ct = default) => OneAtATimeAsync(_cluster, () => CreateCoreAsync(name, year, copyGroups, ct), ct);

    /// <inheritdoc cref="SaveGroupCoreAsync"/>
    public Task<string?> SaveGroupAsync(Guid editionId, GroupInput input, CancellationToken ct = default) => OneAtATimeAsync(_cluster, () => SaveGroupCoreAsync(editionId, input, ct), ct);

    /// <inheritdoc cref="RemoveGroupCoreAsync"/>
    public Task<string?> RemoveGroupAsync(Guid groupId, CancellationToken ct = default) => OneAtATimeAsync(_cluster, () => RemoveGroupCoreAsync(groupId, ct), ct);

    /// <inheritdoc cref="DeleteCoreAsync"/>
    public Task<string?> DeleteAsync(Guid editionId, CancellationToken ct = default) => OneAtATimeAsync(_cluster, () => DeleteCoreAsync(editionId, ct), ct);

    /// <inheritdoc cref="ActivateCoreAsync"/>
    public Task<string?> ActivateAsync(Guid editionId, CancellationToken ct = default) => OneAtATimeAsync(_cluster, () => ActivateCoreAsync(editionId, ct), ct);

    /// <summary>
    /// A new edition, not yet played: it gets the assignments of the active edition, and its groups too when asked
    /// (a group keeps its team and channels; adjust them before the game starts).
    /// </summary>
    /// <returns>The new edition, or why it could not be made.</returns>
    private async Task<(Edition? Edition, string? Error)> CreateCoreAsync(string? name, int year, bool copyGroups, CancellationToken ct)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return (null, "Geef de editie een naam.");
        if (name.Length > Edition.MaxNameLength) return (null, $"De naam mag hooguit {Edition.MaxNameLength} tekens zijn.");
        if (year is < 2000 or > 2200) return (null, "Kies een geldig jaar.");
        var editions = await _db.Editions.ToListAsync(ct);
        if (editions.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, $"Er is al een editie {name}.");
        }

        var folder = NewStorageFolder(editions, name);

        var edition = new Edition
        {
            Name = name, Year = year, Status = EditionStatus.Draft, StorageFolder = folder, CreatedAt = _time.GetUtcNow()
        };
        _db.Editions.Add(edition);

        foreach (var assignment in await _db.Assignments.AsNoTracking().ToListAsync(ct))
        {
            _db.Assignments.Add(new Assignment
            {
                EditionId = edition.Id, Code = assignment.Code, Title = assignment.Title, Description = assignment.Description,
                StandardPoints = assignment.StandardPoints, AwesomePoints = assignment.AwesomePoints
            });
        }

        if (copyGroups)
        {
            foreach (var group in await _db.Groups.AsNoTracking().ToListAsync(ct))
            {
                _db.Groups.Add(new Group
                {
                    EditionId = edition.Id, TeamId = group.TeamId, Name = group.Name, Abbreviation = group.Abbreviation,
                    SlackChannelId = group.SlackChannelId, AdventurersSlackChannelId = group.AdventurersSlackChannelId,
                    MapToken = DatabaseInitializer.NewMapToken(), Description = group.Description, CreatedAt = _time.GetUtcNow()
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Created edition {Edition}", name);
        return (edition, null);
    }

    /// <summary>"_edities/{name}", with a number when another edition has that folder already.</summary>
    internal static string NewStorageFolder(IEnumerable<Edition> editions, string name)
    {
        var folders = editions.Select(e => e.StorageFolder).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folder = $"{EditionsFolderName}/{LocalFileStorageService.FolderName(name)}";
        for (var i = 2; folders.Contains(folder); i++) folder = $"{EditionsFolderName}/{LocalFileStorageService.FolderName(name)}-{i}";
        return folder;
    }

    /// <summary>Adds or changes a group of an edition that is not archived.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    private async Task<string?> SaveGroupCoreAsync(Guid editionId, GroupInput input, CancellationToken ct)
    {
        var edition = await _db.Editions.FirstOrDefaultAsync(e => e.Id == editionId, ct);
        if (edition == null) return "Editie niet gevonden.";
        if (edition.Status == EditionStatus.Archived) return "Een gearchiveerde editie verandert niet meer.";

        var name = input.Name?.Trim();
        var channel = input.SlackChannelId?.Trim();
        var adventurers = Blank(input.AdventurersSlackChannelId);
        if (string.IsNullOrEmpty(name)) return "Geef de groep een naam.";
        if (name.Length > 150) return "De naam mag hooguit 150 tekens zijn.";
        if (string.IsNullOrEmpty(channel)) return "Vul het Slack-kanaal van de groep in.";
        if (channel.Length > 50 || adventurers?.Length > 50) return "Een Slack-kanaal-id is hooguit 50 tekens.";
        if (channel == adventurers) return "Het Avonturiers-kanaal moet een ander kanaal zijn.";
        var abbreviation = Blank(input.Abbreviation);
        if (abbreviation?.Length > 20) return "De afkorting mag hooguit 20 tekens zijn.";

        var others = await AllGroups.Where(g => g.EditionId == editionId && g.Id != input.Id).ToListAsync(ct);
        var channels = others.SelectMany(g => new[] { g.SlackChannelId, g.AdventurersSlackChannelId }).OfType<string>().ToHashSet();
        if (channels.Contains(channel) || (adventurers != null && channels.Contains(adventurers)))
        {
            return "Dat Slack-kanaal is al van een andere groep in deze editie.";
        }

        if (others.Any(g => string.Equals(LocalFileStorageService.FolderName(g.Name), LocalFileStorageService.FolderName(name), StringComparison.OrdinalIgnoreCase)))
        {
            return "Er is al een groep met (bijna) dezelfde naam in deze editie.";
        }

        if (input.TeamId is { } teamId)
        {
            if (!await _db.Teams.AnyAsync(t => t.Id == teamId, ct)) return "Team niet gevonden.";
            if (others.Any(g => g.TeamId == teamId)) return "Dit team speelt al mee in deze editie.";
        }

        // A group of the game being played changes between two Slack events, not during one
        using var closed = edition.Status == EditionStatus.Active ? await PauseSlackAsync(ct) : null;
        if (edition.Status == EditionStatus.Active && closed == null) return Busy;

        Group group;
        if (input.Id is { } id)
        {
            group = await AllGroups.FirstOrDefaultAsync(g => g.Id == id && g.EditionId == editionId, ct) ?? throw new InvalidOperationException("Groep niet gevonden");
            if (input.TeamId is { } team) group.TeamId = team;
        }
        else
        {
            group = new Group { EditionId = editionId, MapToken = DatabaseInitializer.NewMapToken(), CreatedAt = _time.GetUtcNow() };
            if (input.TeamId is { } team) group.TeamId = team;
            _db.Groups.Add(group);
        }

        group.Name = name;
        group.Abbreviation = abbreviation;
        group.SlackChannelId = channel;
        group.AdventurersSlackChannelId = adventurers;
        group.Description = Blank(input.Description);
        await _db.SaveChangesAsync(ct);

        if (edition.Status == EditionStatus.Active)
        {
            await _storage.EnsureFolderStructureAsync(name, ct);
            _changes.NotifyChanged();
        }

        _logger.LogInformation("Saved group {Group} of edition {Edition}", name, edition.Name);
        return null;
    }

    /// <summary>
    /// Takes a group out of an edition. Refused while it has submissions: those would go with it, and open ones would
    /// never be reviewed.
    /// </summary>
    private async Task<string?> RemoveGroupCoreAsync(Guid groupId, CancellationToken ct)
    {
        var group = await AllGroups.Include(g => g.Edition).FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null) return "Groep niet gevonden.";
        if (group.Edition!.Status == EditionStatus.Archived) return "Een gearchiveerde editie verandert niet meer.";

        var submissions = await _db.Submissions.IgnoreQueryFilters().Where(s => s.GroupId == groupId).Select(s => s.Status).ToListAsync(ct);
        if (submissions.Count > 0)
        {
            var open = submissions.Count(s => s is SubmissionStatus.Received or SubmissionStatus.Pending);
            return open > 0
                ? $"{group.Name} heeft nog {open} {(open == 1 ? "inzending" : "inzendingen")} die niet beoordeeld {(open == 1 ? "is" : "zijn")}. Beoordeel of verwijder die eerst."
                : $"{group.Name} heeft al inzendingen in deze editie; die zouden mee verdwijnen.";
        }

        using var closed = group.Edition.Status == EditionStatus.Active ? await PauseSlackAsync(ct) : null;
        if (group.Edition.Status == EditionStatus.Active && closed == null) return Busy;
        _db.Groups.Remove(group);
        await _db.SaveChangesAsync(ct);
        if (group.Edition.Status == EditionStatus.Active) _changes.NotifyChanged();
        _logger.LogInformation("Removed group {Group} from edition {Edition}", group.Name, group.Edition.Name);
        return null;
    }

    /// <summary>Removes an edition that was never played, with its groups and assignments.</summary>
    private async Task<string?> DeleteCoreAsync(Guid editionId, CancellationToken ct)
    {
        var edition = await _db.Editions.FirstOrDefaultAsync(e => e.Id == editionId, ct);
        if (edition == null) return "Editie niet gevonden.";
        if (edition.Status != EditionStatus.Draft) return "Alleen een editie die nog niet gespeeld is kan weg.";

        _db.Groups.RemoveRange(await AllGroups.Where(g => g.EditionId == editionId).ToListAsync(ct));
        _db.Assignments.RemoveRange(await _db.Assignments.IgnoreQueryFilters().Where(a => a.EditionId == editionId).ToListAsync(ct));
        _db.Locations.RemoveRange(await _db.Locations.IgnoreQueryFilters().Where(l => l.EditionId == editionId).ToListAsync(ct));
        _db.Editions.Remove(edition);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted edition {Edition}", edition.Name);
        return null;
    }

    /// <summary>
    /// Starts a prepared edition: the current one is archived with its final standing, and from now on Slack, the pages
    /// and the files are about the new one. Group accounts log in again, to get their group in the new edition.
    /// </summary>
    private async Task<string?> ActivateCoreAsync(Guid editionId, CancellationToken ct)
    {
        using var closed = await PauseSlackAsync(ct);
        if (closed == null) return Busy;

        var edition = await _db.Editions.FirstOrDefaultAsync(e => e.Id == editionId, ct);
        if (edition == null) return "Editie niet gevonden.";
        if (edition.Status != EditionStatus.Draft) return "Alleen een editie die nog niet gespeeld is kan starten.";

        var current = await _db.Editions.FirstOrDefaultAsync(e => e.Status == EditionStatus.Active, ct);
        var standing = current == null ? null : await CurrentStandingAsync(ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            if (current != null)
            {
                current.Status = EditionStatus.Archived;
                current.ArchivedAt = _time.GetUtcNow();
                current.FinalStandingJson = JsonSerializer.Serialize(standing);
                // Saved first: at no moment two editions are active
                await _db.SaveChangesAsync(ct);
            }

            edition.Status = EditionStatus.Active;
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
        });

        _active.Set(edition);
        // Sent while the gate is still closed: the other instances read the new edition before their next live event
        _bus.Publish(ActiveEditionRefresher.ClusterTopic);
        foreach (var group in await _db.Groups.AsNoTracking().ToListAsync(ct))
        {
            await _storage.EnsureFolderStructureAsync(group.Name, ct);
        }

        await LogOutGroupAccountsAsync(ct);
        _changes.NotifyChanged();
        _logger.LogInformation("Edition {Edition} is now active; {Previous} is archived", edition.Name, current?.Name ?? "nothing");
        return null;
    }

    private async Task<List<EditionStanding>> CurrentStandingAsync(CancellationToken ct)
    {
        var stats = await _submissions.GetStatisticsAsync(ct);
        var teams = await _db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.TeamId, ct);
        return stats.GroupStatistics
            .OrderBy(g => g.Rank)
            .Select(g => new EditionStanding(teams.GetValueOrDefault(g.GroupId), g.GroupId, g.GroupName, g.Rank, g.Score))
            .ToList();
    }

    /// <summary>A new security stamp ends every login of the group accounts; the next login gets the new group.</summary>
    private async Task LogOutGroupAccountsAsync(CancellationToken ct)
    {
        var users = await _accounts.Users.Where(u => u.GroupId != null).ToListAsync(ct);
        foreach (var user in users) user.SecurityStamp = Guid.NewGuid().ToString();
        await _accounts.SaveChangesAsync(ct);
        foreach (var user in users) _accountChanges.Notify(user.Id);
    }

    private const string Busy = "Er wordt net opnieuw opgebouwd uit Slack. Probeer het straks nog eens.";

    /// <summary>Holds live Slack processing; null while a rebuild already holds it.</summary>
    private async Task<IDisposable?> PauseSlackAsync(CancellationToken ct)
    {
        try
        {
            return await _gate.CloseAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
