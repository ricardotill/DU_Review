using Microsoft.JSInterop;

namespace ScoutingAutomation.Web.Notifications;

/// <summary>
/// Whether this browser shows toasts and plays a sound for new submissions, kept in localStorage.
/// Toasts are on unless turned off; sound is off unless turned on.
/// One instance per browser tab; the settings menu and the toasts share it.
/// </summary>
public class NotificationPreferences
{
    private readonly IJSRuntime _js;
    private Task? _loading;

    public NotificationPreferences(IJSRuntime js)
    {
        _js = js;
    }

    public event Action? Changed;

    public bool IsLoaded { get; private set; }

    public bool ToastsEnabled { get; private set; } = true;

    public bool SoundEnabled { get; private set; }

    /// <summary>Reads the stored choice once. Only call after the first render: JavaScript is not available before.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public Task SetToastsEnabledAsync(bool enabled) => SaveAsync(enabled, SoundEnabled);

    public async Task SetSoundEnabledAsync(bool enabled)
    {
        await SaveAsync(ToastsEnabled, enabled);
        if (enabled)
        {
            // Lets the person hear how it sounds; this click also allows the browser to play sound
            await PlayChimeAsync();
        }
    }

    public async Task PlayChimeAsync()
    {
        if (!SoundEnabled) return;
        await _js.InvokeVoidAsync("duNotifications.chime");
    }

    private async Task SaveAsync(bool toasts, bool sound)
    {
        await EnsureLoadedAsync();
        ToastsEnabled = toasts;
        SoundEnabled = sound;
        await _js.InvokeVoidAsync("duNotifications.setPrefs", new StoredPreferences(toasts, sound));
        Changed?.Invoke();
    }

    private async Task LoadAsync()
    {
        var stored = await _js.InvokeAsync<StoredPreferences?>("duNotifications.getPrefs");
        if (stored != null)
        {
            ToastsEnabled = stored.Toasts;
            SoundEnabled = stored.Sound;
        }

        IsLoaded = true;
        Changed?.Invoke();
    }

    private sealed record StoredPreferences(bool Toasts, bool Sound);
}
