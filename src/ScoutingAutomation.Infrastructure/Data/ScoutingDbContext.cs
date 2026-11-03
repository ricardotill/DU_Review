using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Infrastructure.Data;

public class ScoutingDbContext : DbContext
{
    public ScoutingDbContext(DbContextOptions<ScoutingDbContext> options) : base(options)
    {
    }

    public const string UniqueFileIndexName = "IX_Submissions_SlackFile";
    public const string FailedEventKeyIndexName = "IX_FailedSlackEvents_EventKey";
    public const string FailedEventStatusIndexName = "IX_FailedSlackEvents_Status";
    public const string BonusEntryGroupIndexName = "IX_BonusPointsEntries_GroupId";
    /// <summary>Unique per edition; the index before editions (on the code alone) was "IX_Assignments_Code".</summary>
    public const string AssignmentCodeIndexName = "IX_Assignments_EditionId_Code";
    public const string GroupChannelIndexName = "IX_Groups_EditionId_SlackChannelId";
    public const string ActiveEditionIndexName = "IX_Editions_OneActive";
    public const string SubmissionAssignmentCodeIndexName = "IX_Submissions_AssignmentCode";
    public const string AdventurersChannelIndexName = "IX_Groups_EditionId_AdventurersSlackChannelId";
    public const string MapTokenIndexName = "IX_Groups_MapToken";
    public const string SubmissionKindIndexName = "IX_Submissions_Kind";
    public const int MaxLocationNameLength = 150;
    public const int MaxLocationHintLength = 1000;
    public const int MaxAssignmentTitleLength = 200;
    public const int MaxAssignmentDescriptionLength = 4000;

    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<FailedSlackEvent> FailedSlackEvents => Set<FailedSlackEvent>();
    public DbSet<BonusPointsEntry> BonusPointsEntries => Set<BonusPointsEntry>();
    public DbSet<GroupBonusPoints> GroupBonusPoints => Set<GroupBonusPoints>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<LocationUnlock> LocationUnlocks => Set<LocationUnlock>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Group
        modelBuilder.Entity<Edition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(Edition.MaxNameLength);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.StorageFolder).HasMaxLength(Edition.MaxNameLength + 20);
            // At most one active edition: it is the one that handles Slack
            entity.HasIndex(e => e.Status).IsUnique().HasDatabaseName(ActiveEditionIndexName).HasFilter("\"Status\" = 'Active'");
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).ValueGeneratedNever();
            entity.Property(t => t.Name).IsRequired().HasMaxLength(150);
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Id).ValueGeneratedOnAdd();
            entity.Property(g => g.Name).IsRequired().HasMaxLength(150);
            entity.Property(g => g.Abbreviation).HasMaxLength(20);
            entity.Property(g => g.SlackChannelId).IsRequired().HasMaxLength(50);
            // Editions never delete their groups by accident: removing an edition or team is refused while groups use it
            entity.HasOne(g => g.Edition).WithMany().HasForeignKey(g => g.EditionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(g => g.Team).WithMany().HasForeignKey(g => g.TeamId).OnDelete(DeleteBehavior.Restrict);
            // A channel belongs to one group per edition; next year it may be used again
            entity.HasIndex(g => new { g.EditionId, g.SlackChannelId }).IsUnique().HasDatabaseName(GroupChannelIndexName);
            entity.Property(g => g.AdventurersSlackChannelId).HasMaxLength(50);
            entity.HasIndex(g => new { g.EditionId, g.AdventurersSlackChannelId }).IsUnique()
                  .HasDatabaseName(AdventurersChannelIndexName)
                  .HasFilter("\"AdventurersSlackChannelId\" IS NOT NULL");
            entity.Property(g => g.MapToken).HasMaxLength(64);
            entity.HasIndex(g => g.MapToken).IsUnique()
                  .HasDatabaseName(MapTokenIndexName)
                  .HasFilter("\"MapToken\" IS NOT NULL");
        });

        // Assignment
        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedOnAdd();
            entity.Property(a => a.Code).IsRequired().HasMaxLength(AssignmentCode.MaxLength);
            entity.HasIndex(a => new { a.EditionId, a.Code }).IsUnique().HasDatabaseName(AssignmentCodeIndexName);
            entity.Property(a => a.Title).HasMaxLength(MaxAssignmentTitleLength);
            entity.Property(a => a.Description).HasMaxLength(MaxAssignmentDescriptionLength);
        });

        // Submission
        modelBuilder.Entity<Submission>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedOnAdd();
            entity.Property(s => s.SlackChannelId).IsRequired().HasMaxLength(50);
            entity.Property(s => s.SlackMessageId).IsRequired().HasMaxLength(50);
            entity.Property(s => s.SlackFileId).HasMaxLength(50);
            entity.Property(s => s.FileName).IsRequired().HasMaxLength(260);
            entity.Property(s => s.StoragePath).IsRequired().HasMaxLength(500);
            entity.Property(s => s.Submitter).IsRequired().HasMaxLength(100);
            entity.Property(s => s.SubmitterName).HasMaxLength(100);
            entity.Ignore(s => s.SubmitterDisplayName);
            entity.Ignore(s => s.CodeLabel);
            // A number, so rows from before kinds existed read as 0: a Toeristen assignment
            // A default in the table, so rows written without the column (older code, raw SQL) are assignments
            entity.Property(s => s.Kind).HasConversion<int>().HasDefaultValueSql("0");
            entity.HasIndex(s => s.Kind).HasDatabaseName(SubmissionKindIndexName);
            entity.Property(s => s.ContentType).HasMaxLength(100);
            entity.Property(s => s.Status).HasConversion<string>();

            entity.HasOne(s => s.Group)
                  .WithMany(g => g.Submissions)
                  .HasForeignKey(s => s.GroupId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.SlackChannelId, s.SlackMessageId });
            // One submission per Slack file, also when an event arrives twice at the same time
            entity.HasIndex(s => new { s.SlackChannelId, s.SlackMessageId, s.SlackFileId })
                  .IsUnique()
                  .HasDatabaseName(UniqueFileIndexName)
                  .HasFilter("\"SlackFileId\" IS NOT NULL");
            entity.HasIndex(s => s.Status);
            entity.Property(s => s.AssignmentCode).HasMaxLength(AssignmentCode.MaxLength);
            entity.HasIndex(s => s.AssignmentCode).HasDatabaseName(SubmissionAssignmentCodeIndexName);
        });

        // Review
        modelBuilder.Entity<Review>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedOnAdd();
            entity.Property(r => r.Reviewer).IsRequired().HasMaxLength(100);
            entity.Property(r => r.Status).HasConversion<string>();
            entity.Property(r => r.ReactionEmoji).HasMaxLength(50);

            entity.HasOne(r => r.Submission)
                  .WithMany(s => s.Reviews)
                  .HasForeignKey(r => r.SubmissionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => r.SubmissionId);
        });

        // FailedSlackEvent
        modelBuilder.Entity<FailedSlackEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.EventKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(30);
            entity.Property(e => e.ChannelId).HasMaxLength(50);
            entity.Property(e => e.MessageTs).HasMaxLength(50);
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.Error).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(e => e.EventKey).IsUnique().HasDatabaseName(FailedEventKeyIndexName);
            entity.HasIndex(e => e.Status).HasDatabaseName(FailedEventStatusIndexName);
        });

        // BonusPointsEntry
        modelBuilder.Entity<BonusPointsEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            // The column kept its name from when the categories were fixed
            entity.Property(e => e.CategoryKey).HasColumnName("Category").IsRequired().HasMaxLength(BonusCategoryConfig.MaxKeyLength);

            entity.HasOne(e => e.Group)
                  .WithMany()
                  .HasForeignKey(e => e.GroupId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.GroupId).HasDatabaseName(BonusEntryGroupIndexName);
        });

        // Location
        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(l => new { l.EditionId, l.Number });
            entity.Property(l => l.Number).ValueGeneratedNever();
            entity.Property(l => l.Name).IsRequired().HasMaxLength(MaxLocationNameLength);
            entity.Property(l => l.Hint).HasMaxLength(MaxLocationHintLength);
        });

        // LocationUnlock
        modelBuilder.Entity<LocationUnlock>(entity =>
        {
            entity.HasKey(u => new { u.GroupId, u.LocationNumber });
            entity.HasOne(u => u.Group)
                  .WithMany()
                  .HasForeignKey(u => u.GroupId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // GroupBonusPoints
        modelBuilder.Entity<GroupBonusPoints>(entity =>
        {
            entity.HasKey(e => new { e.GroupId, e.CategoryKey });
            entity.Property(e => e.CategoryKey).HasMaxLength(BonusCategoryConfig.MaxKeyLength);

            entity.HasOne(e => e.Group)
                  .WithMany()
                  .HasForeignKey(e => e.GroupId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        ApplyActiveEditionFilters(modelBuilder);
    }

    /// <summary>
    /// Every query sees the active edition only, so code written for one game keeps working unchanged. A query over
    /// other editions (conversion, an archive view) says so with <c>IgnoreQueryFilters()</c>.
    /// </summary>
    private void ApplyActiveEditionFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Group>().HasQueryFilter(g => Editions.Any(e => e.Id == g.EditionId && e.Status == EditionStatus.Active));
        modelBuilder.Entity<Assignment>().HasQueryFilter(a => Editions.Any(e => e.Id == a.EditionId && e.Status == EditionStatus.Active));
        modelBuilder.Entity<Location>().HasQueryFilter(l => Editions.Any(e => e.Id == l.EditionId && e.Status == EditionStatus.Active));
        modelBuilder.Entity<Submission>().HasQueryFilter(s => Editions.Any(e => e.Id == s.Group!.EditionId && e.Status == EditionStatus.Active));
        modelBuilder.Entity<Review>().HasQueryFilter(r => Editions.Any(e => e.Id == r.Submission!.Group!.EditionId && e.Status == EditionStatus.Active));
        modelBuilder.Entity<GroupBonusPoints>().HasQueryFilter(b => Editions.Any(e => e.Id == b.Group!.EditionId && e.Status == EditionStatus.Active));
        modelBuilder.Entity<BonusPointsEntry>().HasQueryFilter(b => Editions.Any(e => e.Id == b.Group!.EditionId && e.Status == EditionStatus.Active));
        modelBuilder.Entity<LocationUnlock>().HasQueryFilter(u => Editions.Any(e => e.Id == u.Group!.EditionId && e.Status == EditionStatus.Active));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AssignEditionAndTeam(ActiveEditionId());
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (NeedsEditionOrTeam()) AssignEditionAndTeam(await ActiveEditionIdAsync(cancellationToken));
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private IEnumerable<IEditionScoped> AddedWithoutEdition() => ChangeTracker.Entries()
        .Where(e => e.State == EntityState.Added)
        .Select(e => e.Entity)
        .OfType<IEditionScoped>()
        .Where(e => e.EditionId == Guid.Empty);

    private IEnumerable<Group> AddedGroupsWithoutTeam() => ChangeTracker.Entries<Group>()
        .Where(e => e.State == EntityState.Added && e.Entity.TeamId == Guid.Empty)
        .Select(e => e.Entity);

    private bool NeedsEditionOrTeam() => AddedWithoutEdition().Any() || AddedGroupsWithoutTeam().Any();

    /// <summary>
    /// New rows go to the active edition unless they name one, and a new group without a team gets its own team, with
    /// the group's id. So the code that adds groups, assignments and locations needs no edition of its own.
    /// </summary>
    private void AssignEditionAndTeam(Guid? activeEditionId)
    {
        foreach (var row in AddedWithoutEdition().ToList())
        {
            row.EditionId = activeEditionId ?? throw new InvalidOperationException("No active edition");
        }

        foreach (var group in AddedGroupsWithoutTeam().ToList())
        {
            Teams.Add(new Team { Id = group.Id, Name = group.Name, CreatedAt = group.CreatedAt });
            group.TeamId = group.Id;
        }
    }

    private Guid? ActiveEditionId() =>
        NeedsEditionOrTeam() ? LocalActiveEdition() ?? Editions.Where(e => e.Status == EditionStatus.Active).Select(e => (Guid?)e.Id).FirstOrDefault() ?? AddFirstEdition() : null;

    private async Task<Guid?> ActiveEditionIdAsync(CancellationToken ct) =>
        LocalActiveEdition()
        ?? await Editions.Where(e => e.Status == EditionStatus.Active).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct)
        ?? AddFirstEdition();

    private Guid? LocalActiveEdition() =>
        Editions.Local.FirstOrDefault(e => e.Status == EditionStatus.Active)?.Id;

    /// <summary>
    /// A database without any edition (just made, or a test) gets one when the first group or assignment is stored.
    /// The app makes the first edition itself at start (DatabaseInitializer), with the year of the game.
    /// </summary>
    private Guid AddFirstEdition()
    {
        var year = DateTimeOffset.UtcNow.Year;
        var edition = new Edition { Name = Edition.DefaultName(year), Year = year, Status = EditionStatus.Active };
        Editions.Add(edition);
        return edition.Id;
    }
}