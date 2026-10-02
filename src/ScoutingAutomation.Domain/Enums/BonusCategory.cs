namespace ScoutingAutomation.Domain.Enums;

/// <summary>Point categories outside the Slack assignments, entered by hand per group.</summary>
public enum BonusCategory
{
    Adventurers,
    TravelAgency
}

public static class BonusCategoryExtensions
{
    public static string Label(this BonusCategory category) => category switch
    {
        BonusCategory.Adventurers => "Avonturiers",
        _ => "Reisbureau (pubquiz)"
    };

    /// <summary>For narrow places such as table headers and CSV columns.</summary>
    public static string ShortLabel(this BonusCategory category) => category switch
    {
        BonusCategory.Adventurers => "Avonturiers",
        _ => "Reisbureau"
    };
}
