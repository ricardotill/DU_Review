namespace ScoutingAutomation.Domain.Entities;

public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Abbreviation { get; set; }
    public string SlackChannelId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Submission> Submissions { get; set; } = new List<Submission>();
}
