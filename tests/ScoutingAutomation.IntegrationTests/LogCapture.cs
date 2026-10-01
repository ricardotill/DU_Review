using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>Collects what the app logs, for tests that check a message or wait for one.</summary>
public sealed class LogCapture(LogLevel minimumLevel = LogLevel.Error) : ILoggerProvider
{
    private readonly List<string> _messages = new();
    private readonly LogLevel _minimumLevel = minimumLevel;

    public IReadOnlyList<string> Messages
    {
        get { lock (_messages) return _messages.ToList(); }
    }

    /// <summary>Waits until a log message contains <paramref name="text"/>, for work the app does in the background.</summary>
    public async Task WaitForAsync(string text, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Messages.Any(m => m.Contains(text)))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Never logged: {text}");
            await Task.Delay(20);
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= capture._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (capture._messages) capture._messages.Add(formatter(state, exception));
            }
        }
    }
}
