namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Marks a page a group login may open with only its team: also a team that does not play the active edition (its
/// earlier editions). A marked page must itself limit the login to its own team (<see cref="AppAuth.TeamId"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TeamAccessAttribute : Attribute;
