using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Infrastructure.Data;

public class ScoutingDbContext : DbContext
{
    public ScoutingDbContext(DbContextOptions<ScoutingDbContext> options) : base(options)
    {
    }

    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<Review> Reviews => Set<Review>();

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
        });

        // Assignment
        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedOnAdd();
            entity.Property(a => a.Number).IsRequired();
            entity.HasIndex(a => a.Number).IsUnique();
        });

        // Submission
        modelBuilder.Entity<Submission>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedOnAdd();
            entity.Property(s => s.SlackChannelId).IsRequired().HasMaxLength(50);
            entity.Property(s => s.SlackMessageId).IsRequired().HasMaxLength(50);
            entity.Property(s => s.FileName).IsRequired().HasMaxLength(260);
            entity.Property(s => s.StoragePath).IsRequired().HasMaxLength(500);
            entity.Property(s => s.Submitter).IsRequired().HasMaxLength(100);
            entity.Property(s => s.ContentType).HasMaxLength(100);
            entity.Property(s => s.Status).HasConversion<string>();

            entity.HasOne(s => s.Group)
                  .WithMany(g => g.Submissions)
                  .HasForeignKey(s => s.GroupId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.SlackChannelId, s.SlackMessageId });
            entity.HasIndex(s => s.Status);
            entity.HasIndex(s => s.AssignmentNumber);
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
    }
}
