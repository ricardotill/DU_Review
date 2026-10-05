namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Marks a page or endpoint that a group login may open as well. Everything without it is for the jury and admins
/// only (see <see cref="GroupAccessHandler"/>). A marked page must itself limit a group to its own data.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class GroupAccessAttribute : Attribute;
