using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class LiveComponentBaseTests : BunitContext
{
    private readonly SubmissionChangeNotifier _notifier = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SubmissionChangeNotifier>.Instance);
    private readonly ListLoggerProvider _logs = new();

    public LiveComponentBaseTests()
    {
        Services.AddSingleton<ISubmissionChangeNotifier>(_notifier);
        Services.AddLogging(b => b.AddProvider(_logs));
    }

    [Fact]
    public void Burst_ShouldCauseOneReload()
    {
        var cut = Render<ProbeComponent>();
        cut.Instance.Loads.Should().Be(1);

        _notifier.NotifyChanged();
        _notifier.NotifyChanged();
        _notifier.NotifyChanged();

        cut.WaitForAssertion(() => cut.Markup.Should().Be("loads:2"), TimeSpan.FromSeconds(5));
        Thread.Sleep(500);
        cut.Instance.Loads.Should().Be(2);
    }

    // A failed reload does not render, so bUnit's WaitFor helpers would never re-check: poll instead
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        condition().Should().BeTrue();
    }

    [Fact]
    public async Task FailingReload_ShouldBeLoggedAndKeepPageAlive()
    {
        var cut = Render<ProbeComponent>();
        cut.Instance.OnLoad = _ => throw new InvalidOperationException("database gone");

        _notifier.NotifyChanged();

        await EventuallyAsync(() => _logs.Messages.Any(m => m.Contains("Live reload of ProbeComponent failed")));
        cut.Markup.Should().Be("loads:1");
    }

    [Fact]
    public async Task ReloadOfClosedPage_ShouldBeIgnoredSilently()
    {
        var cut = Render<ProbeComponent>();
        cut.Instance.OnLoad = _ => throw new ObjectDisposedException("scope");

        _notifier.NotifyChanged();

        await EventuallyAsync(() => cut.Instance.Loads == 2);
        await Task.Delay(100);
        _logs.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosingPage_ShouldCancelPendingReloadAndUnsubscribe()
    {
        var cut = Render<ProbeComponent>();
        var probe = cut.Instance;

        _notifier.NotifyChanged();
        await DisposeComponentsAsync();
        _notifier.NotifyChanged();

        await Task.Delay(600);
        probe.Loads.Should().Be(1);
        _logs.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task NotificationRacingWithClose_ShouldBeIgnored()
    {
        var cut = Render<ProbeComponent>();
        var probe = cut.Instance;
        await DisposeComponentsAsync();

        // A notification that was already on its way when the page closed
        probe.RaiseChanged();

        await Task.Delay(500);
        probe.Loads.Should().Be(1);
    }

    private sealed class ProbeComponent : LiveComponentBase
    {
        public int Loads { get; private set; }
        public Func<int, Task>? OnLoad { get; set; }

        protected override Task OnInitializedAsync() => ReloadAsync();

        protected override async Task LoadAsync(IServiceProvider services)
        {
            Loads++;
            if (OnLoad != null) await OnLoad(Loads);
        }

        public void RaiseChanged() =>
            typeof(LiveComponentBase).GetMethod("OnSubmissionsChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(this, null);

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, $"loads:{Loads}");
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ListLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && category.Contains("LiveComponentBase");

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    lock (provider.Messages) provider.Messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
