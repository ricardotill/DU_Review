namespace ScoutingAutomation.Contracts.Configuration;

public class ScoutingSettings
{
    public const string SectionName = "Scouting";

    public string StorageRoot { get; set; } = "./scouting_storage";
    public string AssignmentPointsFile { get; set; } = "./opdrachtenpunten.csv";

    /// <summary>
    /// Manual mode at startup: files are only downloaded and named, nothing else is processed.
    /// Can be switched on the dashboard while the app runs.
    /// </summary>
    public bool ManualMode { get; set; }

    /// <summary>Where settings changed in the app (such as reviewers added on the fly) are saved.</summary>
    public string RuntimeSettingsFile { get; set; } = "./runtime-settings.json";

    /// <summary>
    /// Time zone of the game, e.g. "Europe/Amsterdam". File names and times on the pages use it,
    /// whatever time zone the server runs in.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Amsterdam";

    /// <summary>
    /// Optional start of the event, in the game's time zone (<see cref="TimeZone"/>), e.g. "2026-10-03T09:00".
    /// Slack messages posted before it are ignored. Without it, every message counts.
    /// Can be changed in the app while it runs.
    /// </summary>
    public DateTime? EventStart { get; set; }

    /// <summary>
    /// Optional end of the event, in the game's time zone (<see cref="TimeZone"/>), e.g. "2026-10-03T17:00".
    /// Submissions posted from then on do not count; the bot replies with <see cref="SlackFeedbackConfig.TooLate"/>.
    /// Without it, the game never ends. Can be changed in the app while it runs.
    /// </summary>
    public DateTime? EventEnd { get; set; }

    /// <summary>Accounts: the first admin, created at the first start.</summary>
    public AccountSettings Accounts { get; set; } = new();

    /// <summary>The SMTP server for invitations and password reset mails.</summary>
    public EmailSettings Email { get; set; } = new();

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

    /// <summary>
    /// The message posted in every group channel when the game starts.
    /// </summary>
    public GameStartAnnouncementConfig GameStartAnnouncement { get; set; } = new();

    /// <summary>
    /// The message posted in every group channel when the game ends.
    /// </summary>
    public GameEndAnnouncementConfig GameEndAnnouncement { get; set; } = new();

    /// <summary>
    /// Whether a group may still send an attempt for an assignment it already has approved or awesome.
    /// When true, such an attempt is not stored and the bot replies with <see cref="SlackFeedbackConfig.AlreadyCompleted"/>.
    /// Can be switched in the app while it runs.
    /// </summary>
    public bool BlockAttemptsAfterApproval { get; set; }

    /// <summary>The reminder in the jury channel for submissions that wait too long for a review.</summary>
    public ReviewReminderConfig ReviewReminder { get; set; } = new();

    public List<GroupConfig> Groups { get; set; } = new();

    /// <summary>
    /// Slack user IDs whose review reactions change a submission's status (and so its score).
    /// Reactions from anyone else are ignored.
    /// </summary>
    public List<string> Reviewers { get; set; } = new();

    /// <summary>Optional names per reviewer ID, e.g. "U04ABC123": "Eva". Names set in the app take priority.</summary>
    public Dictionary<string, string> ReviewerNames { get; set; } = new();
    public ReviewStatusReactionsConfig ReviewStatusReactions { get; set; } = new();
    public SlackSettings Slack { get; set; } = new();

    /// <summary>
    /// Point categories outside the Slack assignments, entered by hand per group (e.g. a pubquiz).
    /// Null when the section is left out: then <see cref="BonusCategoryConfig.Defaults"/> apply.
    /// Categories added or removed in the app win over these.
    /// </summary>
    public List<BonusCategoryConfig>? BonusCategories { get; set; }

    /// <summary>
    /// The second game part on Slack: each group's Avonturiers post location check-ins and theme photos in their own channel.
    /// Off unless <see cref="AdventurersSettings.Enabled"/>: then the app works as before and ignores those channels.
    /// </summary>
    public AdventurersSettings Adventurers { get; set; } = new();

    /// <summary>
    /// The address the app is reached at from outside, e.g. "https://du.example.nl", for links in Slack messages
    /// (such as a group's map). Without it the links are relative and only work for someone already in the app.
    /// </summary>
    public string? PublicUrl { get; set; }
}

public class BonusCategoryConfig
{
    /// <summary>The longest key; the database column holds this many characters.</summary>
    public const int MaxKeyLength = 20;

    /// <summary>Stays the same for good: the points are stored under it. Lower case letters, digits and dashes.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The name in the app, e.g. "Reisbureau (pubquiz)".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>For narrow places such as the home page; the name when left out.</summary>
    public string? ShortName { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayShortName => string.IsNullOrWhiteSpace(ShortName) ? Name : ShortName;

    /// <summary>The categories of the first game, for settings without a BonusCategories section.</summary>
    public static IReadOnlyList<BonusCategoryConfig> Defaults { get; } = new[]
    {
        new BonusCategoryConfig { Key = "avonturiers", Name = "Avonturiers", ShortName = "Av" },
        new BonusCategoryConfig { Key = "reisbureau", Name = "Reisbureau (pubquiz)", ShortName = "Rb" }
    };
}

public class GroupConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short code used in stored file names, e.g. "CDW" gives "CDW 16 14-32-05.jpg".
    /// </summary>
    public string? Abbreviation { get; set; }
    public string SlackChannelId { get; set; } = string.Empty;

    /// <summary>The Slack channel of the group's Avonturiers; empty when the group has none.</summary>
    public string? AdventurersSlackChannelId { get; set; }
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

    /// <summary>
    /// A new attempt for an assignment the group already completed, when <see cref="ScoutingSettings.BlockAttemptsAfterApproval"/> is on.
    /// Placeholder <c>{number}</c> is replaced by the assignment number.
    /// </summary>
    public FeedbackConfig AlreadyCompleted { get; set; } = new()
    {
        Reaction = "lock",
        Message = "Opdracht {number} is al goedgekeurd. Een nieuwe poging telt niet meer en is niet opgeslagen."
    };

    /// <summary>
    /// A submission posted after <see cref="ScoutingSettings.EventEnd"/>; it is not stored.
    /// Placeholder <c>{end}</c> is replaced by the end time, e.g. "17:00".
    /// </summary>
    public FeedbackConfig TooLate { get; set; } = new()
    {
        Reaction = "alarm_clock",
        Message = "Het spel is om {end} afgelopen. Deze inzending telt niet meer mee en is niet opgeslagen."
    };
}

public class ReviewReminderConfig
{
    /// <summary>The Slack channel of the jury. The bot must be a member. Empty means no reminders.</summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Minutes a submission may wait for a review before the jury is reminded. Null means no reminders.
    /// Can be changed in the app while it runs.
    /// </summary>
    public int? AfterMinutes { get; set; }
}

public class GameStartAnnouncementConfig
{
    /// <summary>
    /// Post the message by itself when <see cref="ScoutingSettings.EventStart"/> passes.
    /// An admin can always post it by hand in the app.
    /// </summary>
    public bool AutoPost { get; set; } = true;

    /// <summary>
    /// Slack text of the message; leave empty to post nothing. Placeholders: <c>{group}</c> is the group's name.
    /// <c>{approved}</c>, <c>{awesome}</c> and <c>{rejected}</c> are the review emoji from <see cref="ScoutingSettings.ReviewStatusReactions"/>.
    /// <c>{missingNumber}</c>, <c>{unknownNumber}</c>, <c>{unsupportedFile}</c>, <c>{downloadFailed}</c> and <c>{alreadyCompleted}</c>
    /// are the reactions from <see cref="ScoutingSettings.SlackFeedback"/>. <c>{retryRule}</c> says whether a group may send
    /// a new attempt after approval, following <see cref="ScoutingSettings.BlockAttemptsAfterApproval"/>.
    /// </summary>
    public string? Message { get; set; } = DefaultMessage;

    public const string DefaultMessage =
        ":checkered_flag: *Het spel is begonnen, {group}!* Vanaf nu kun je inzendingen sturen.\n\n" +
        "*Zo stuur je een inzending*\n" +
        "• Plaats een bericht in dit kanaal met je foto, video, geluidsopname, PDF of Word-document als bijlage.\n" +
        "• Zet het opdrachtnummer in het bericht, bijvoorbeeld \"Opdracht 12\".\n" +
        "• Meerdere bestanden voor dezelfde opdracht mogen in één bericht.\n" +
        "• {retryRule}\n\n" +
        "*Wat betekenen de emoji's?*\n" +
        "• {approved} goedgekeurd\n" +
        "• {awesome} awesome, extra goed gedaan!\n" +
        "• {rejected} afgekeurd, probeer het opnieuw\n" +
        "• {missingNumber} geen of onbekend opdrachtnummer: plaats je inzending opnieuw met het juiste nummer\n" +
        "• {unsupportedFile} bestand niet ondersteund of niet op te halen: plaats je inzending opnieuw";

    /// <summary>What <c>{retryRule}</c> becomes when a new attempt after approval is allowed.</summary>
    public const string RetryAllowedRule = "Opnieuw insturen mag: je beste inzending telt.";

    /// <summary>What <c>{retryRule}</c> becomes when a new attempt after approval is blocked. The emoji placeholders are filled in too.</summary>
    public const string RetryBlockedRule =
        "Opnieuw insturen mag tot je opdracht is goedgekeurd. Na {approved} of {awesome} is de opdracht klaar: een nieuwe poging wordt niet nagekeken ({alreadyCompleted}).";
}

public class GameEndAnnouncementConfig
{
    /// <summary>
    /// Post the message by itself when <see cref="ScoutingSettings.EventEnd"/> passes.
    /// An admin can always post it by hand in the app.
    /// </summary>
    public bool AutoPost { get; set; } = true;

    /// <summary>
    /// Slack text of the message; leave empty to post nothing. Placeholders: <c>{group}</c> is the group's name,
    /// <c>{end}</c> the end time, e.g. "17:00".
    /// </summary>
    public string? Message { get; set; } = DefaultMessage;

    public const string DefaultMessage =
        ":stopwatch: *Het spel is afgelopen, {group}!* Inzendingen vanaf {end} tellen niet meer mee.\n\n" +
        "De jury beoordeelt nog de inzendingen die op tijd binnen waren. Bedankt voor het meedoen!";
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
    /// <summary>
    /// Nothing goes to Slack and Slack's signature is not checked: for trying the app out only. Off unless set,
    /// so a missing or broken Slack section never leaves the webhook open to fake reviews.
    /// </summary>
    public bool SimulationMode { get; set; }

    /// <summary>
    /// Receive events over an outgoing WebSocket instead of HTTP, so no public HTTPS address is needed.
    /// </summary>
    public bool SocketMode { get; set; }
}

public class AccountSettings
{
    /// <summary>
    /// The email address of the first admin. When no admin account exists at startup, one is made for this address;
    /// without it (and without any admin) the app does not start, unless no mail server is set either: then the
    /// account "admin" with password "admin" is made.
    /// </summary>
    public string? BootstrapAdminEmail { get; set; }

    /// <summary>
    /// Optional password for that first admin. Without it, a link to choose one is written to the log (and mailed).
    /// </summary>
    public string? BootstrapAdminPassword { get; set; }
}

public enum SmtpSecurity
{
    /// <summary>Upgrade to TLS after connecting (usually port 587).</summary>
    StartTls,

    /// <summary>TLS from the start (usually port 465).</summary>
    SslOnConnect,

    /// <summary>No encryption: only for a mail server on the same machine or a test server.</summary>
    None
}

public class EmailSettings
{
    /// <summary>The SMTP server; without it, mails are only written to the log.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>The sender address, e.g. "noreply@du.example.nl".</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "DU Review";

    /// <summary>Whether mails can be sent; otherwise they go to the log.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
