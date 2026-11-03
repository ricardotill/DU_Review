namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Marks a page or endpoint that the beamer account may open as well, such as the scoreboard. Everything without it is
/// closed to the beamer (see <see cref="GroupAccessHandler"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class BeamerAccessAttribute : Attribute;
