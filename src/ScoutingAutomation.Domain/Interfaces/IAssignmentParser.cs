namespace ScoutingAutomation.Domain.Interfaces;

/// <param name="AssignmentCode">Normalised, see <see cref="Models.AssignmentCode"/>.</param>
public record AssignmentParseResult(bool Success, string? AssignmentCode, string? ErrorMessage = null)
{
    public static AssignmentParseResult Succeeded(string code) => new(true, code);
    public static AssignmentParseResult Failed(string reason) => new(false, null, reason);
}

public interface IAssignmentParser
{
    AssignmentParseResult Parse(string? messageText);
}
