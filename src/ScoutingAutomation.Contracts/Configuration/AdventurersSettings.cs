namespace ScoutingAutomation.Contracts.Configuration;

/// <summary>
/// The Avonturiers game part on Slack: check-ins at locations, theme rounds and broadcasts. Texts, rounds, times and
/// the locations open at the start can also be changed in the app; those changes win over these settings.
/// </summary>
public class AdventurersSettings
{
    /// <summary>Off: every Avonturiers page is hidden and messages in the Avonturiers channels are ignored.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Toeristen assignments open from the start: the lowest numbers. The other numbered assignments open as the
    /// Avonturiers check in, in the same proportion: with k of the L locations counted, the first
    /// <c>InitialOpenAssignments + (all - InitialOpenAssignments) * k / L</c> (rounded down) are open.
    /// A check-in counts from the end of the first round that ends after it was posted. Letter assignments are always open.
    /// </summary>
    public int InitialOpenAssignments { get; set; } = 30;

    /// <summary>
    /// Locations 1 through this number are open for every group at the start. The organisation raises the number per
    /// group during the game; a check-in at a location above it is stored but only counts once the location opens.
    /// </summary>
    public int InitialOpenLocations { get; set; } = 10;

    /// <summary>
    /// Super locations ('supercoördinaten'): an approved check-in there counts as two locations for opening assignments.
    /// Can also be changed in the app.
    /// </summary>
    public SuperLocationsConfig SuperLocations { get; set; } = new();

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
        ":camera_with_flash: *Ronde {round} is begonnen!* Nu: {themes}. Stuur je foto of video vóór {end} met \"Opdracht\" en het nummer erbij.\n{message}";

    /// <summary>Posted in every Avonturiers channel when a round ends. Placeholders: {round}, {themes}, {end}.</summary>
    public string? RoundCloseMessage { get; set; } =
        ":stopwatch: *Ronde {round} is gesloten.* Inzendingen voor {themes} tellen niet meer mee. De uitslag volgt zodra de jury alles heeft bekeken.";

    /// <summary>
    /// Posted instead of the closing and the opening when a round starts as the previous one ends. Leave empty to post
    /// those two separately. Placeholders: {previousRound}, {previousThemes} (the round that ends), {round}, {themes},
    /// {start}, {end}, {message} (the round that starts).
    /// </summary>
    public string? RoundHandoverMessage { get; set; } =
        ":stopwatch: *Ronde {previousRound} is gesloten.* Inzendingen voor {previousThemes} tellen niet meer mee. De uitslag volgt zodra de jury alles heeft bekeken.\n\n" +
        ":camera_with_flash: *Ronde {round} is begonnen!* Nu: {themes}. Stuur je foto of video vóór {end} met \"Opdracht\" en het nummer erbij.\n{message}";

    /// <summary>Posted in every Avonturiers channel once every submission of a closed round is reviewed. Placeholders: {round}, {ranking}.</summary>
    public string? RoundResultMessage { get; set; } = ":trophy: *Uitslag ronde {round}:* {ranking}";

    /// <summary>
    /// Posted in the group's Toeristen channel when its Avonturiers opened new assignments.
    /// Placeholders: {count} (open assignments), {total} (all numbered assignments), {assignment} (the sentence naming the new ones).
    /// </summary>
    public string? UnlockNoticeMessage { get; set; } =
        ":unlock: Jullie Avonturiers hebben nieuwe opdrachten vrijgespeeld! {assignment} Jullie hebben nu {count} van de {total} opdrachten open.";

    /// <summary>Posted in the Toeristen channel when the number of open assignments went down. Placeholders: {count}, {total}.</summary>
    public string? UnlockCorrectionMessage { get; set; } =
        ":leftwards_arrow_with_hook: Correctie: een check-in van jullie Avonturiers telt toch niet. Jullie hebben nu {count} van de {total} opdrachten open.";

    /// <summary>Map tiles for the map pages; any tile server that serves {z}/{x}/{y} works.</summary>
    public string MapTileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>The credit the tile server asks for, shown on the map (HTML allowed).</summary>
    public string MapAttribution { get; set; } = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a>-bijdragers";

    public const string DefaultStartMessage =
        ":world_map: *Welkom Avonturiers van {group}!*\n\n" +
        "• Op jullie kaart staan de locaties die vrijgespeeld zijn: {map}\n" +
        "• Maak op elke locatie een foto met *alle* lopers en zichtbare scoutingkleding of das, en zet het locatienummer erbij, bijvoorbeeld \"Locatie 7\".\n" +
        "• Elke goedgekeurde locatie speelt opdrachten vrij voor jullie Toeristen.\n" +
        "• Elk half uur komt er een half-uur opdracht. Stuur je foto met het nummer, bijvoorbeeld \"Opdracht 3\".\n\n" +
        "{approved} goedgekeurd, {rejected} afgekeurd: probeer het opnieuw.";

    public const string DefaultFinalMessage =
        ":round_pushpin: *De eindlocatie, {group}!* Kom om {time} naar Destination Unknown. Bekijk de kaart: {map}";
}

public class SuperLocationsConfig
{
    /// <summary>Off: every location counts once.</summary>
    public bool Enabled { get; set; }

    /// <summary>The location numbers that count double, e.g. [10, 20, 30]. A number that is no location does nothing.</summary>
    public List<int> Numbers { get; set; } = new();
}

public class AdventurersAnnouncementConfig
{
    /// <summary>Post the message by itself at <see cref="PostAt"/>. An admin can always post it by hand.</summary>
    public bool AutoPost { get; set; } = true;

    /// <summary>
    /// When to post, in the game's time zone, e.g. "2026-10-03T12:00". Also the start (start information) or end
    /// (final location) of the Avonturiers part: their messages count from the start until the end, and groups see their
    /// map from the start. Empty: the event's start or end. Can be changed in the app.
    /// </summary>
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
        Message = "Er staat geen locatie- of opdrachtnummer in je bericht. Plaats je foto opnieuw met bijvoorbeeld \"Locatie 7\" of \"Opdracht 3\"."
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
        Message = "Half-uur opdracht {number} hoort bij geen enkele ronde en telt niet mee."
    };

    /// <summary>Placeholders <c>{number}</c> and <c>{round}</c>. The photo is stored, but does not count.</summary>
    public FeedbackConfig ThemeOutsideRound { get; set; } = new()
    {
        Reaction = "hourglass",
        Message = "Half-uur opdracht {number} hoort bij ronde {round} en telt nu niet meer."
    };

    /// <summary>Placeholder <c>{number}</c>. The photo is stored, but does not count.</summary>
    public FeedbackConfig NoRoundOpen { get; set; } = new()
    {
        Reaction = "hourglass",
        Message = "Er loopt nu geen ronde: half-uur opdracht {number} telt niet mee."
    };

    /// <summary>A check-in at a location that is not open for the group yet: stored, but it does not count. Placeholder <c>{number}</c>.</summary>
    public FeedbackConfig LocationNotUnlocked { get; set; } = new()
    {
        Reaction = "construction",
        Message = "Locatie {number} is nog niet open voor jullie. De check-in telt zodra de organisatie de locatie opent."
    };

    /// <summary>In the Toeristen channel, for an assignment that is not open yet: not stored. Placeholder <c>{number}</c>.</summary>
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
