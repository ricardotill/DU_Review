using Microsoft.AspNetCore.Components.Authorization;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Runs what an admin button does, after checking on the server that the tab is still in admin mode. A button only
/// shown to admins is not enough: a tab that lost admin mode (the admin key was regenerated) could still send the click.
/// A failed save shows as a message instead of breaking the page.
/// </summary>
public static class AdminActions
{
    public const string NotAdmin = "Alleen in beheermodus. Ga naar Beheer om de beheerderssleutel in te vullen.";

    /// <summary>Whether the user of this tab is an admin now; follows key changes while the tab is open.</summary>
    public static async Task<bool> IsAdminAsync(Task<AuthenticationState>? state) =>
        state != null && (await state).User.IsInRole(KeyAuth.AdminRole);

    /// <returns>Null when done, otherwise the message to show.</returns>
    public static async Task<string?> RunAsync(Task<AuthenticationState>? state, Func<string?> action)
    {
        if (!await IsAdminAsync(state)) return NotAdmin;

        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Opslaan is mislukt: {ex.Message}";
        }
    }

    /// <inheritdoc cref="RunAsync(Task{AuthenticationState}?, Func{string?})"/>
    public static Task<string?> RunAsync(Task<AuthenticationState>? state, Action action) =>
        RunAsync(state, () =>
        {
            action();
            return (string?)null;
        });

    /// <inheritdoc cref="RunAsync(Task{AuthenticationState}?, Func{string?})"/>
    public static async Task<string?> RunTaskAsync(Task<AuthenticationState>? state, Func<Task<string?>> action)
    {
        if (!await IsAdminAsync(state)) return NotAdmin;

        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"Dat is mislukt: {ex.Message}";
        }
    }
}
