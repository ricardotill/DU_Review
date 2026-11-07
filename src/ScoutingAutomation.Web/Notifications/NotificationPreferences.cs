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
    private readonly string _storageKey;
    private Task? _loading;

    public NotificationPreferences(IJSRuntime js) : this(js, "du_notifications")
    {
    }

    /// <param name="storageKey">The localStorage key: the jury's and a group's choices are kept apart.</param>
    protected NotificationPreferences(IJSRuntime js, string storageKey)
    {
        _js = js;
        _storageKey = storageKey;
    }

    public event Action? Changed;

    public bool IsLoaded { get; private set; }

    public bool ToastsEnabled { get; private set; } = true;

    public bool SoundEnabled { get; private set; }

    /// <summary>
    /// A notification of the phone or computer itself while the tab is in the background. Only on when chosen, and
    /// only works once the browser allowed it.
    /// </summary>
    public bool SystemEnabled { get; private set; }

    /// <summary>Reads the stored choice once. Only call after the first render: JavaScript is not available before.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public Task SetToastsEnabledAsync(bool enabled) => SaveAsync(enabled, SoundEnabled, SystemEnabled);

    public async Task SetSoundEnabledAsync(bool enabled)
    {
        await SaveAsync(ToastsEnabled, enabled, SystemEnabled);
        if (enabled)
        {
            // Lets the person hear how it sounds; this click also allows the browser to play sound
            await PlayChimeAsync();
        }
    }

    /// <summary>Turns the system notification on (asking the browser for permission) or off.</summary>
    /// <returns>The browser's answer: "granted", "denied", "default" or "unsupported".</returns>
    public async Task<string> SetSystemEnabledAsync(bool enabled)
    {
        var permission = await SystemPermissionAsync();
        if (enabled && permission == "default")
        {
            // Asked inside this click: browsers only ask when the person did something
            permission = await _js.InvokeAsync<string>("duNotifications.requestSystemPermission");
        }

        await SaveAsync(ToastsEnabled, SoundEnabled, enabled && permission == "granted");
        return permission;
    }

    /// <summary>What the browser allows: "granted", "denied", "default" (not asked yet) or "unsupported".</summary>
    public async Task<string> SystemPermissionAsync() => await _js.InvokeAsync<string>("duNotifications.systemPermission");

    public async Task PlayChimeAsync()
    {
        if (!SoundEnabled) return;
        await _js.InvokeVoidAsync("duNotifications.chime");
    }

    private async Task SaveAsync(bool toasts, bool sound, bool system)
    {
        await EnsureLoadedAsync();
        ToastsEnabled = toasts;
        SoundEnabled = sound;
        SystemEnabled = system;
        await _js.InvokeVoidAsync("duNotifications.setPrefs", new StoredPreferences(toasts, sound, system), _storageKey);
        Changed?.Invoke();
    }

    private async Task LoadAsync()
    {
        var stored = await _js.InvokeAsync<StoredPreferences?>("duNotifications.getPrefs", _storageKey);
        if (stored != null)
        {
            ToastsEnabled = stored.Toasts;
            SoundEnabled = stored.Sound;
            SystemEnabled = stored.System;
        }

        IsLoaded = true;
        Changed?.Invoke();
    }

    private sealed record StoredPreferences(bool Toasts, bool Sound, bool System = false);
}

/// <summary>A group's choices for the notices of newly opened assignments, kept apart from the jury's.</summary>
public sealed class GroupNotificationPreferences : NotificationPreferences
{
    public GroupNotificationPreferences(IJSRuntime js) : base(js, "du_notifications_group")
    {
    }
}
