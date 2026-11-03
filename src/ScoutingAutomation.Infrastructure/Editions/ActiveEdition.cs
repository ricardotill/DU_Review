using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Infrastructure.Editions;

/// <summary>
/// The edition being played, for the parts of the app that are not in the database: the folder files go to, and the
/// name pages show. Set at start (DatabaseInitializer) and when an admin switches editions (EditionService).
/// </summary>
public sealed class ActiveEdition
{
    private volatile Snapshot _current = new(Guid.Empty, "", null);

    public sealed record Snapshot(Guid Id, string Name, string? StorageFolder);

    public Snapshot Current => _current;

    /// <summary>Raised after the active edition changed; from any thread.</summary>
    public event Action? Changed;

    public void Set(Edition edition)
    {
        var next = new Snapshot(edition.Id, edition.Name, edition.StorageFolder);
        var changed = next.Id != _current.Id;
        _current = next;
        if (changed) Changed?.Invoke();
    }
}
