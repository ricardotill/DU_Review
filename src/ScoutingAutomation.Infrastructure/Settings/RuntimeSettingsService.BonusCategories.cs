using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Infrastructure.Settings;

/// <summary>
/// The point categories entered by hand (Avonturiers, Reisbureau, ...). They come from appsettings; once an admin
/// adds, renames or removes one in the app, the whole list is saved and wins over appsettings until it is reset.
/// Removing a category hides its points but keeps them: adding the same key again brings them back.
/// While the Avonturiers part is on, its category (<see cref="AdventurersSettings.FinalGameCategoryKey"/>) is always
/// there and cannot be removed: it shows the computed Avonturiers points, and holds the hand-entered final game.
/// </summary>
public partial class RuntimeSettingsService
{
    /// <summary>More categories than this do not fit the tables.</summary>
    public const int MaxBonusCategories = 12;

    public const int MaxBonusCategoryNameLength = 60;
    public const int MaxBonusCategoryShortNameLength = 12;

    // Null: the list from appsettings applies
    private List<BonusCategoryConfig>? _bonusCategories;

    /// <summary>The categories in use, in the order they are shown.</summary>
    public IReadOnlyList<BonusCategoryConfig> BonusCategories
    {
        get { lock (_lock) return Current(); }
    }

    /// <summary>The Avonturiers category: on while that part is on, and then never removed.</summary>
    public bool IsFixedBonusCategory(string key) => FixedBonusKey is { } fixedKey && key == fixedKey;

    /// <summary>The key of the Avonturiers category; null while the Avonturiers part is off.</summary>
    public string? FixedBonusKey => AdventurersEnabled ? AdventurersOptions.FinalGameCategoryKey : null;

    /// <summary>The list in use, with the Avonturiers category first when it is missing. Call inside the lock.</summary>
    private List<BonusCategoryConfig> Current()
    {
        var list = Copy(_bonusCategories ?? ConfiguredBonusCategories);
        if (FixedBonusKey is { } key && list.All(c => c.Key != key))
        {
            list.Insert(0, new BonusCategoryConfig { Key = key, Name = "Avonturiers", ShortName = "Av" });
        }

        return list;
    }

    /// <summary>Whether the categories were changed in the app.</summary>
    public bool IsBonusCategoriesChanged
    {
        get { lock (_lock) return _bonusCategories != null; }
    }

    private IReadOnlyList<BonusCategoryConfig> ConfiguredBonusCategories =>
        _options.Value.BonusCategories ?? (IReadOnlyList<BonusCategoryConfig>)BonusCategoryConfig.Defaults;

    /// <returns>Null when added, otherwise why not.</returns>
    public string? AddBonusCategory(string? key, string? name, string? shortName = null)
    {
        key = NormalizeBonusKey(key);
        var category = new BonusCategoryConfig { Key = key, Name = name?.Trim() ?? "", ShortName = Blank(shortName) };
        if (CheckBonusCategory(category) is { } error) return error;

        lock (_lock)
        {
            var list = Current();
            if (list.Any(c => c.Key == key)) return $"De sleutel '{key}' is al in gebruik.";
            if (list.Count >= MaxBonusCategories) return $"Er kunnen hoogstens {MaxBonusCategories} categorieën zijn.";

            list.Add(category);
            _bonusCategories = list;
            Save();
        }

        _logger.LogWarning("Bonus category {Key} ({Name}) added in the app", key, category.Name);
        Changed?.Invoke();
        return null;
    }

    /// <returns>Null when saved, otherwise why not.</returns>
    public string? RenameBonusCategory(string key, string? name, string? shortName = null)
    {
        var renamed = new BonusCategoryConfig { Key = key, Name = name?.Trim() ?? "", ShortName = Blank(shortName) };
        if (CheckBonusCategory(renamed) is { } error) return error;

        lock (_lock)
        {
            var list = Current();
            var index = list.FindIndex(c => c.Key == key);
            if (index < 0) return $"Categorie '{key}' bestaat niet.";
            if (list[index].Name == renamed.Name && list[index].ShortName == renamed.ShortName) return null;

            list[index] = renamed;
            _bonusCategories = list;
            Save();
        }

        _logger.LogWarning("Bonus category {Key} renamed to {Name} in the app", key, renamed.Name);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Stops using a category: its points no longer count or show, but stay stored.</summary>
    /// <returns>False when there was no such category, or it is the Avonturiers category, which stays.</returns>
    public bool RemoveBonusCategory(string key)
    {
        if (IsFixedBonusCategory(key))
        {
            _logger.LogWarning("Refused to remove bonus category {Key}: it belongs to the Avonturiers", key);
            return false;
        }

        lock (_lock)
        {
            var list = Current();
            if (list.RemoveAll(c => c.Key == key) == 0) return false;

            _bonusCategories = list;
            Save();
        }

        _logger.LogWarning("Bonus category {Key} removed in the app; its points are kept", key);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Goes back to the categories from appsettings.</summary>
    public void ResetBonusCategories()
    {
        lock (_lock)
        {
            if (_bonusCategories == null) return;
            _bonusCategories = null;
            Save();
        }

        _logger.LogWarning("Bonus categories reset to appsettings");
        Changed?.Invoke();
    }

    /// <summary>Why a category cannot be used; null when it is fine. Also checks the ones from appsettings at start.</summary>
    public static string? CheckBonusCategory(BonusCategoryConfig category)
    {
        if (!BonusKeyPattern().IsMatch(category.Key ?? ""))
        {
            return $"De sleutel '{category.Key}' mag alleen kleine letters, cijfers en streepjes bevatten (hoogstens {BonusCategoryConfig.MaxKeyLength} tekens).";
        }

        if (string.IsNullOrWhiteSpace(category.Name)) return "Geef de categorie een naam.";
        if (category.Name.Length > MaxBonusCategoryNameLength) return $"De naam mag hoogstens {MaxBonusCategoryNameLength} tekens zijn.";
        if (category.ShortName?.Length > MaxBonusCategoryShortNameLength)
        {
            return $"De korte naam mag hoogstens {MaxBonusCategoryShortNameLength} tekens zijn.";
        }

        return null;
    }

    /// <summary>A key typed by an admin: "Foto Wedstrijd" becomes "foto-wedstrijd".</summary>
    public static string NormalizeBonusKey(string? key) =>
        Regex.Replace((key ?? "").Trim().ToLowerInvariant(), @"\s+", "-");

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<BonusCategoryConfig> Copy(IEnumerable<BonusCategoryConfig> categories) =>
        categories.Select(c => new BonusCategoryConfig { Key = c.Key, Name = c.Name, ShortName = c.ShortName }).ToList();

    /// <summary>Saved categories that are no longer valid (a hand-edited file) are dropped, so the rest still loads.</summary>
    private void LoadBonusCategories(List<BonusCategoryConfig>? saved)
    {
        _bonusCategories = saved?
            .Where(c => c != null && CheckBonusCategory(c) == null)
            .DistinctBy(c => c.Key)
            .Take(MaxBonusCategories)
            .ToList();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,19}$")]
    private static partial Regex BonusKeyPattern();
}
