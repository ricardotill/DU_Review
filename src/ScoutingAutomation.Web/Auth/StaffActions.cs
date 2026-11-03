using Microsoft.AspNetCore.Components.Authorization;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Checks on the server that the user of a tab is jury or admin before a jury button acts, like
/// <see cref="AdminActions"/> does for admins: a group never sees these buttons, but a tab could still send the click.
/// </summary>
public static class StaffActions
{
    public const string NotStaff = "Alleen voor de jury en beheerders.";

    public static async Task<bool> IsStaffAsync(Task<AuthenticationState>? state) =>
        state != null && (await state).User.IsInRole(KeyAuth.JuryRole);

    /// <returns>Null when done, otherwise the message to show.</returns>
    public static async Task<string?> RunAsync(Task<AuthenticationState>? state, Func<string?> action)
    {
        if (!await IsStaffAsync(state)) return NotStaff;

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
        if (!await IsStaffAsync(state)) return NotStaff;

        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"Opslaan is mislukt: {ex.Message}";
        }
    }
}
