using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Slack.Events;

/// <summary>
/// Shows every error the app logs on the events page, so the organisers see on the day itself that something went
/// wrong (a failed announcement, rebuild, save or download), not only in a log file nobody watches.
/// </summary>
public sealed class EventLogLoggerProvider : ILoggerProvider
{
    /// <summary>For an error the events page already shows in another way, such as a failed Slack event.</summary>
    public static readonly EventId AlreadyShown = new(4100, "AlreadyShownOnEventsPage");

    private readonly IServiceProvider _services;
    private SlackEventLog? _eventLog;

    [ThreadStatic] private static bool _writing;

    // The event log itself logs; it is looked up on first use, after the logging system is built
    public EventLogLoggerProvider(IServiceProvider services)
    {
        _services = services;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, EventId eventId, string message, Exception? exception)
    {
        // An error while adding an entry must not add another entry, and so on
        if (_writing || eventId == AlreadyShown) return;

        _writing = true;
        try
        {
            _eventLog ??= _services.GetService<SlackEventLog>();
            var detail = exception == null || message.Contains(exception.Message, StringComparison.Ordinal)
                ? message
                : $"{message}: {exception.Message}";
            _eventLog?.AddNotice(SlackEventKind.System, SlackEventOutcome.Failed, detail, category[(category.LastIndexOf('.') + 1)..]);
        }
        catch (ObjectDisposedException)
        {
            // The app is stopping
        }
        finally
        {
            _writing = false;
        }
    }

    private sealed class Logger(EventLogLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, eventId, formatter(state, exception), exception);
        }
    }
}
