namespace ScoutingAutomation.Contracts.Configuration;

/// <summary>
/// The Avonturiers game part on Slack: check-ins at locations, theme rounds and broadcasts. Texts, rounds, times,
/// the unlock pool and the location check can also be changed in the app; those changes win over these settings.
/// </summary>
public class AdventurersSettings
{
    /// <summary>Off: every Avonturiers page is hidden and messages in the Avonturiers channels are ignored.</summary>
    public bool Enabled { get; set; }

    /// <summary>Warn the jury when a group checks in at a location that was not unlocked for it on the dashboard.</summary>
    public bool RequireUnlockedLocation { get; set; }

    /// <summary>
    /// Toeristen assignment codes in the order approved check-ins unlock them. Empty: only the number of unlocked
    /// assignments is counted (the Toeristen pick from a list on paper) and no assignment is ever locked.
    /// </summary>
    public List<string> UnlockPool { get; set; } = new();

    /// <summary>The most points the theme rounds give a group together.</summary>
    public int ThemeMaxPoints { get; set; } = 14;

    /// <summary>The most points of the final game (entered by hand) that count.</summary>
    public int FinalMaxPoints { get; set; } = 14;

    /// <summary>The bonus category whose hand-entered points are the final game (and the own-transport start bonus).</summary>
    public string FinalGameCategoryKey { get; set; } = "avonturiers";

    /// <summary>Length of a theme round made by the generator, in minutes.</summary>
    public int RoundMinutes { get; set; } = 30;

    /// <summary>Rounds to start with; once rounds are changed in the app, those are used instead.</summary>
    public List<ThemeRoundConfig> Rounds { get; set; } = new();

    /// <summary>The start information for every Avonturiers channel.</summary>
    public AdventurersAnnouncementConfig StartAnnouncement { get; set; } = new() { Message = DefaultStartMessage };

    /// <summary>The final location ('Destination Unknown') for every Avonturiers channel.</summary>
    public AdventurersAnnouncementConfig FinalAnnouncement { get; set; } = new() { Message = DefaultFinalMessage };

    /// <summary>Reactions and thread replies of the bot in the Avonturiers channels (and the lock in the Toeristen channel).</summary>
    public AdventurersFeedbackConfig Feedback { get; set; } = new();

    /// <summary>Posted in every Avonturiers channel when a round opens. Placeholders: {round}, {themes}, {start}, {end}, {message}.</summary>
    public string? RoundOpenMessage { get; set; } =
        ":camera_with_flash: *Ronde {round} is begonnen!* Het thema is {themes}. Stuur je foto of video vóór {end} met \"Thema\" en het nummer erbij.\n{message}";

    /// <summary>Posted in every Avonturiers channel when a round ends. Placeholders: {round}, {themes}, {end}.</summary>
    public string? RoundCloseMessage { get; set; } =
        ":stopwatch: *Ronde {round} is gesloten.* Inzendingen voor {themes} tellen niet meer mee. De uitslag volgt zodra de jury alles heeft bekeken.";

    /// <summary>Posted in every Avonturiers channel once every submission of a closed round is reviewed. Placeholders: {round}, {ranking}.</summary>
    public string? RoundResultMessage { get; set; } = ":trophy: *Uitslag ronde {round}:* {ranking}";

    /// <summary>
    /// Posted in the group's Toeristen channel when its Avonturiers unlocked an assignment.
    /// Placeholders: {count}, {total}, {assignment} (in pool mode the sentence naming the new assignment, otherwise empty).
    /// </summary>
    public string? UnlockNoticeMessage { get; set; } =
        ":unlock: Jullie Avonturiers hebben opdracht {count} van {total} vrijgespeeld! {assignment}";

    /// <summary>Posted in the Toeristen channel when an approval was taken back. Placeholders: {count}, {total}.</summary>
    public string? UnlockCorrectionMessage { get; set; } =
        ":leftwards_arrow_with_hook: Correctie: een check-in van jullie Avonturiers telt toch niet. Jullie hebben nu {count} van {total} opdrachten vrijgespeeld.";

    /// <summary>Map tiles for the map pages; any tile server that serves {z}/{x}/{y} works.</summary>
    public string MapTileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>The credit the tile server asks for, shown on the map (HTML allowed).</summary>
    public string MapAttribution { get; set; } = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a>-bijdragers";

    public const string DefaultStartMessage =
        ":world_map: *Welkom Avonturiers van {group}!*\n\n" +
        "• Op jullie kaart staan de locaties die vrijgespeeld zijn: {map}\n" +
        "• Maak op elke locatie een foto met *alle* lopers en zichtbare scoutingkleding of das, en zet het locatienummer erbij, bijvoorbeeld \"Locatie 7\".\n" +
        "• Elke goedgekeurde locatie speelt een opdracht vrij voor jullie Toeristen.\n" +
        "• Elk half uur komt er een thema-opdracht. Stuur je foto met het themanummer, bijvoorbeeld \"Thema 3\".\n\n" +
        "{approved} goedgekeurd, {rejected} afgekeurd: probeer het opnieuw.";

    public const string DefaultFinalMessage =
        ":round_pushpin: *De eindlocatie, {group}!* Kom om {time} naar Destination Unknown. Bekijk de kaart: {map}";
}

public class AdventurersAnnouncementConfig
{
    /// <summary>Post the message by itself at <see cref="PostAt"/>. An admin can always post it by hand.</summary>
    public bool AutoPost { get; set; } = true;

    /// <summary>When to post, in the game's time zone, e.g. "2026-10-03T12:00". Can be changed in the app.</summary>
    public DateTime? PostAt { get; set; }

    /// <summary>Slack text; placeholders <c>{group}</c>, <c>{map}</c> (the group's map link) and <c>{time}</c>.</summary>
    public string? Message { get; set; }
}

/// <summary>Leave <see cref="FeedbackConfig.Reaction"/> or <see cref="FeedbackConfig.Message"/> empty to skip that part.</summary>
public class AdventurersFeedbackConfig
{
    public FeedbackConfig MissingNumber { get; set; } = new()
    {
        Reaction = "question",
        Message = "Er staat geen locatie- of themanummer in je bericht. Plaats je foto opnieuw met bijvoorbeeld \"Locatie 7\" of \"Thema 3\"."
    };

    /// <summary>Placeholder <c>{number}</c>.</summary>
    public FeedbackConfig UnknownLocation { get; set; } = new()
    {
        Reaction = "question",
        Message = "Locatie {number} bestaat niet. Controleer het nummer en plaats je foto opnieuw."
    };

    /// <summary>Placeholder <c>{number}</c>. The photo is stored, but counts for no round.</summary>
    public FeedbackConfig UnknownTheme { get; set; } = new()
    {
        Reaction = "question",
        Message = "Thema {number} hoort bij geen enkele ronde en telt niet mee."
    };

    /// <summary>Placeholders <c>{number}</c> and <c>{round}</c>. The photo is stored, but does not count.</summary>
    public FeedbackConfig ThemeOutsideRound { get; set; } = new()
    {
        Reaction = "hourglass",
        Message = "Thema {number} hoort bij ronde {round} en telt nu niet meer."
    };

    /// <summary>Placeholder <c>{number}</c>. The photo is stored, but does not count.</summary>
    public FeedbackConfig NoRoundOpen { get; set; } = new()
    {
        Reaction = "hourglass",
        Message = "Er loopt nu geen ronde: thema {number} telt niet mee."
    };

    /// <summary>For the jury, when <see cref="AdventurersSettings.RequireUnlockedLocation"/> is on. Placeholder <c>{number}</c>.</summary>
    public FeedbackConfig LocationNotUnlocked { get; set; } = new()
    {
        Reaction = "construction",
        Message = "Let op jury: locatie {number} is voor deze groep nog niet vrijgespeeld."
    };

    /// <summary>In the Toeristen channel, in pool mode, for an assignment that is not unlocked yet. Placeholder <c>{number}</c>.</summary>
    public FeedbackConfig AssignmentLocked { get; set; } = new()
    {
        Reaction = "lock",
        Message = "Opdracht {number} is nog niet vrijgespeeld door jullie Avonturiers. Deze inzending telt niet en is niet opgeslagen."
    };
}

/// <summary>One theme round: the theme numbers that count, and when.</summary>
public class ThemeRoundConfig
{
    public int Number { get; set; }

    /// <summary>Start, in the game's time zone.</summary>
    public DateTime Start { get; set; }

    /// <summary>End (exclusive), in the game's time zone; the start plus the round length when left out.</summary>
    public DateTime? End { get; set; }

    /// <summary>The theme numbers of this round, e.g. ["3"].</summary>
    public List<string> Themes { get; set; } = new();

    /// <summary>Extra text for the opening message, e.g. what the theme asks for.</summary>
    public string? Message { get; set; }
}
