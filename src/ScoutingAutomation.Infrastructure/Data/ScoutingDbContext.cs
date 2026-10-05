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
    public const string AssignmentCodeIndexName = "IX_Assignments_Code";
    public const string SubmissionAssignmentCodeIndexName = "IX_Submissions_AssignmentCode";
    public const string AdventurersChannelIndexName = "IX_Groups_AdventurersSlackChannelId";
    public const string MapTokenIndexName = "IX_Groups_MapToken";
    public const string SubmissionKindIndexName = "IX_Submissions_Kind";
    public const int MaxLocationNameLength = 150;
    public const int MaxLocationHintLength = 1000;
    public const int MaxAssignmentTitleLength = 200;
    public const int MaxAssignmentDescriptionLength = 4000;

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
        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Id).ValueGeneratedOnAdd();
            entity.Property(g => g.Name).IsRequired().HasMaxLength(150);
            entity.Property(g => g.Abbreviation).HasMaxLength(20);
            entity.Property(g => g.SlackChannelId).IsRequired().HasMaxLength(50);
            entity.HasIndex(g => g.SlackChannelId).IsUnique();
            entity.Property(g => g.AdventurersSlackChannelId).HasMaxLength(50);
            entity.HasIndex(g => g.AdventurersSlackChannelId).IsUnique()
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
            entity.HasIndex(a => a.Code).IsUnique().HasDatabaseName(AssignmentCodeIndexName);
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
            entity.HasKey(l => l.Number);
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
    }
}
