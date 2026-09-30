namespace ScoutingAutomation.Contracts.Configuration;

public class ScoutingSettings
{
    public const string SectionName = "Scouting";

    public string StorageRoot { get; set; } = "./scouting_storage";
    public string AssignmentPointsFile { get; set; } = "./opdrachtenpunten.csv";
    // Lists are left empty here: the configuration binder appends to default items,
    // which would make it impossible to remove a value through appsettings.
    public List<string> SupportedExtensions { get; set; } = new();

    /// <summary>
    /// MIME type prefixes that are always accepted, so media with an unlisted extension still counts.
    /// </summary>
    public List<string> SupportedMimeTypePrefixes { get; set; } = new();

    /// <summary>
    /// Automatic reactions and thread replies the bot places on problematic submissions.
    /// </summary>
    public SlackFeedbackConfig SlackFeedback { get; set; } = new();

    public List<GroupConfig> Groups { get; set; } = new();

    /// <summary>
    /// Slack user IDs whose review reactions change a submission's status (and so its score).
    /// Reactions from anyone else are ignored.
    /// </summary>
    public List<string> Reviewers { get; set; } = new();
    public ReviewStatusReactionsConfig ReviewStatusReactions { get; set; } = new();
    public SlackSettings Slack { get; set; } = new();
}

public class GroupConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short code used in stored file names, e.g. "CDW" gives "CDW 16.jpg".
    /// </summary>
    public string? Abbreviation { get; set; }
    public string SlackChannelId { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class SlackFeedbackConfig
{
    /// <summary>Placeholder <c>{files}</c> is replaced by the unsupported file names.</summary>
    public FeedbackConfig UnsupportedFile { get; set; } = new()
    {
        Reaction = "warning",
        Message = "Dit bestandstype wordt niet ondersteund: {files}. Stuur een foto, video, geluidsopname, PDF of Word-document."
    };

    public FeedbackConfig MissingAssignmentNumber { get; set; } = new()
    {
        Reaction = "question",
        Message = "Er staat geen opdrachtnummer in je bericht. Plaats je inzending opnieuw met het nummer erbij, bijvoorbeeld \"Opdracht 12\"."
    };

    /// <summary>Placeholder <c>{number}</c> is replaced by the assignment number from the message.</summary>
    public FeedbackConfig UnknownAssignmentNumber { get; set; } = new()
    {
        Reaction = "question",
        Message = "Opdracht {number} bestaat niet. Controleer het nummer en plaats je inzending opnieuw."
    };

    /// <summary>Placeholder <c>{files}</c> is replaced by the file names that could not be downloaded.</summary>
    public FeedbackConfig DownloadFailed { get; set; } = new()
    {
        Reaction = "warning",
        Message = "Het bestand {files} kon niet worden opgehaald. Plaats je inzending opnieuw."
    };
}

/// <summary>
/// Leave <see cref="Reaction"/> or <see cref="Message"/> empty to skip that part.
/// </summary>
public class FeedbackConfig
{
    public string? Reaction { get; set; }
    public string? Message { get; set; }
}

public class ReviewStatusReactionsConfig
{
    // Empty defaults: the configuration binder appends to default items (see SupportedExtensions)
    public List<string> Approved { get; set; } = new();
    public List<string> Rejected { get; set; } = new();
    public List<string> Awesome { get; set; } = new();
}

public class SlackSettings
{
    public string? BotToken { get; set; }
    public string? SigningSecret { get; set; }
    /// <summary>App-level token (xapp-...) with connections:write, used for Socket Mode.</summary>
    public string? AppToken { get; set; }
    public bool SimulationMode { get; set; } = true;

    /// <summary>
    /// Receive events over an outgoing WebSocket instead of HTTP, so no public HTTPS address is needed.
    /// </summary>
    public bool SocketMode { get; set; }
}
