using Microsoft.AspNetCore.Identity;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>The Identity error messages people can see, in Dutch.</summary>
public sealed class DutchIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error(nameof(DefaultError), "Er ging iets mis.");
    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), $"Er is al een account met {email}.");
    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), $"Er is al een account met {userName}.");
    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), $"{email} is geen geldig e-mailadres.");
    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), $"{userName} is geen geldig e-mailadres.");
    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), "Deze link is verlopen of al gebruikt. Vraag een nieuwe aan.");
    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), "Het huidige wachtwoord klopt niet.");
    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), $"Een wachtwoord heeft minstens {length} tekens.");
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Error(nameof(PasswordRequiresUniqueChars), $"Een wachtwoord heeft minstens {uniqueChars} verschillende tekens.");
    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), "Een wachtwoord heeft minstens één cijfer.");
    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), "Een wachtwoord heeft minstens één kleine letter.");
    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), "Een wachtwoord heeft minstens één hoofdletter.");
    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric), "Een wachtwoord heeft minstens één leesteken.");
    public override IdentityError UserAlreadyHasPassword() => Error(nameof(UserAlreadyHasPassword), "Dit account heeft al een wachtwoord.");

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
