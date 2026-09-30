namespace ScoutingAutomation.Domain.Interfaces;

public record AssignmentParseResult(bool Success, int? AssignmentNumber, string? ErrorMessage = null)
{
    public static AssignmentParseResult Succeeded(int number) => new(true, number);
    public static AssignmentParseResult Failed(string reason) => new(false, null, reason);
}

public interface IAssignmentParser
{
    AssignmentParseResult Parse(string? messageText);
}
