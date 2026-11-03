using Microsoft.AspNetCore.Identity;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>An account: logs in with its email address (also its user name) and a password.</summary>
public class AppUser : IdentityUser<Guid>
{
    public const int MaxDisplayNameLength = 100;

    /// <summary>The name shown in the app, e.g. "Eva" or the group's name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The group of a group account; null for jury, beamer and admins.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>A blocked account cannot log in; its sessions end at once.</summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }
}
