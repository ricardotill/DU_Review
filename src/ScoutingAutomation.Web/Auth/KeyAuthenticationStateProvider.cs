using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using ScoutingAutomation.Infrastructure.Access;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// The login state of an open browser tab. When a key is regenerated, open tabs are checked right away:
/// they lose admin rights, or are sent to the login page, without waiting for their next page load.
/// </summary>
public sealed class KeyAuthenticationStateProvider : ServerAuthenticationStateProvider, IDisposable
{
    private readonly AccessKeyService _keys;

    public KeyAuthenticationStateProvider(AccessKeyService keys)
    {
        _keys = keys;
        _keys.Changed += OnKeysChanged;
    }

    private void OnKeysChanged() => _ = RevalidateAsync();

    public async Task RevalidateAsync()
    {
        AuthenticationState state;
        try
        {
            state = await GetAuthenticationStateAsync();
        }
        catch (InvalidOperationException)
        {
            // No login state yet: nothing to withdraw
            return;
        }

        var validated = KeyAuth.Revalidate(state.User, _keys) ?? new ClaimsPrincipal(new ClaimsIdentity());
        if (!ReferenceEquals(validated, state.User))
        {
            SetAuthenticationState(Task.FromResult(new AuthenticationState(validated)));
        }
    }

    public void Dispose() => _keys.Changed -= OnKeysChanged;
}
