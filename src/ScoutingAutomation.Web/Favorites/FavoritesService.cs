using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.JSInterop;
using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Web.Favorites;

/// <summary>
/// Favorite submissions of this browser, kept in the "du_favorites" cookie.
/// One instance per browser tab; every star on the page shares it.
/// </summary>
public partial class FavoritesService
{
    /// <summary>A cookie holds about 4 KB; 300 keys of 11 characters stay well within it.</summary>
    public const int MaxFavorites = 300;

    private readonly IJSRuntime _js;
    private HashSet<string> _keys = new(StringComparer.Ordinal);
    private Task? _loading;

    public FavoritesService(IJSRuntime js)
    {
        _js = js;
    }

    public event Action? Changed;

    public bool IsLoaded { get; private set; }

    public int Count => _keys.Count;

    /// <summary>
    /// A short key that stays the same when submissions are rebuilt from Slack:
    /// it depends on the Slack message and file, not on the database ID.
    /// </summary>
    public static string KeyFor(Submission submission)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{submission.SlackChannelId}|{submission.SlackMessageId}|{submission.FileName}"));
        return Convert.ToBase64String(hash, 0, 8).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Reads the cookie once. Only call after the first render: JavaScript is not available before.
    /// Never throws: when the cookie cannot be read, there are no favorites, and a lost connection is tried again next time.
    /// </summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public bool IsFavorite(Submission submission) => _keys.Contains(KeyFor(submission));

    /// <returns>False when the submission could not be added because the limit is reached.</returns>
    public async Task<bool> ToggleAsync(Submission submission)
    {
        await EnsureLoadedAsync();
        var key = KeyFor(submission);
        var keys = new HashSet<string>(_keys, StringComparer.Ordinal);
        if (!keys.Remove(key))
        {
            if (keys.Count >= MaxFavorites) return false;
            keys.Add(key);
        }

        // Changed only when the cookie took it, so the stars never show what the browser does not keep
        try
        {
            await _js.InvokeVoidAsync("duFavorites.set", string.Join('.', keys));
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            return true;
        }

        _keys = keys;
        Changed?.Invoke();
        return true;
    }

    private async Task LoadAsync()
    {
        string? cookie;
        try
        {
            cookie = await _js.InvokeAsync<string>("duFavorites.get");
        }
        catch (JSException)
        {
            // The script failed: no favorites, rather than pages that stop working
            cookie = "";
        }
        catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // Not connected (yet): the next call tries again
            _loading = null;
            return;
        }

        // Only keys this app makes; anything else in the cookie is ignored
        _keys = new HashSet<string>(
            (cookie ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries).Where(k => KeyPattern().IsMatch(k)).Take(MaxFavorites),
            StringComparer.Ordinal);
        IsLoaded = true;
        Changed?.Invoke();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex KeyPattern();
}
