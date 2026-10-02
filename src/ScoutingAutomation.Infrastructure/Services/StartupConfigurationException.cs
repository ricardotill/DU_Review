namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// A setting the app cannot start with, such as a broken assignment points file or two groups sharing one folder.
/// Unlike a database that is not reachable yet, waiting does not fix it: the app stops with this message.
/// </summary>
public sealed class StartupConfigurationException(string message, Exception? inner = null) : Exception(message, inner);
