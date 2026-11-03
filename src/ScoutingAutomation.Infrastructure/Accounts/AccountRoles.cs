namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>The roles an account can have. An admin also has <see cref="Jury"/>.</summary>
public static class AccountRoles
{
    public const string Admin = "Admin";

    /// <summary>Jury and organisers: every page, admins included.</summary>
    public const string Jury = "Jury";

    /// <summary>A contestant group: only pages marked for groups, and only its own data.</summary>
    public const string Group = "Group";

    /// <summary>The beamer screen: only the scoreboard and the slideshow.</summary>
    public const string Beamer = "Beamer";

    public static IReadOnlyList<string> All { get; } = [Admin, Jury, Group, Beamer];
}

/// <summary>What an account is, as chosen in the account list; decides its roles.</summary>
public enum AccountKind
{
    Jury,
    Admin,
    Group,
    Beamer
}

public static class AccountKinds
{
    public static IReadOnlyList<string> RolesOf(AccountKind kind) => kind switch
    {
        AccountKind.Admin => [AccountRoles.Admin, AccountRoles.Jury],
        AccountKind.Jury => [AccountRoles.Jury],
        AccountKind.Group => [AccountRoles.Group],
        AccountKind.Beamer => [AccountRoles.Beamer],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static AccountKind FromRoles(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet();
        if (set.Contains(AccountRoles.Admin)) return AccountKind.Admin;
        if (set.Contains(AccountRoles.Jury)) return AccountKind.Jury;
        if (set.Contains(AccountRoles.Group)) return AccountKind.Group;
        return AccountKind.Beamer;
    }

    public static string Label(this AccountKind kind) => kind switch
    {
        AccountKind.Admin => "Beheerder",
        AccountKind.Jury => "Jury",
        AccountKind.Group => "Groep",
        AccountKind.Beamer => "Beamer",
        _ => kind.ToString()
    };
}
