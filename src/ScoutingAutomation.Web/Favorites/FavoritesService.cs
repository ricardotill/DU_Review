using System.Security.Cryptography;
using System.Text;
using Microsoft.JSInterop;
using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Web.Favorites;

/// <summary>
/// Favorite submissions of this browser, kept in the "du_favorites" cookie.
/// One instance per browser tab; every star on the page shares it.
/// </summary>
public class FavoritesService
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

    /// <summary>Reads the cookie once. Only call after the first render: JavaScript is not available before.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public bool IsFavorite(Submission submission) => _keys.Contains(KeyFor(submission));

    /// <returns>False when the submission could not be added because the limit is reached.</returns>
    public async Task<bool> ToggleAsync(Submission submission)
    {
        await EnsureLoadedAsync();
        var key = KeyFor(submission);
        if (!_keys.Remove(key))
        {
            if (_keys.Count >= MaxFavorites) return false;
            _keys.Add(key);
        }

        await _js.InvokeVoidAsync("duFavorites.set", string.Join('.', _keys));
        Changed?.Invoke();
        return true;
    }

    private async Task LoadAsync()
    {
        var cookie = await _js.InvokeAsync<string>("duFavorites.get");
        _keys = new HashSet<string>(
            (cookie ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries).Take(MaxFavorites),
            StringComparer.Ordinal);
        IsLoaded = true;
        Changed?.Invoke();
    }
}
