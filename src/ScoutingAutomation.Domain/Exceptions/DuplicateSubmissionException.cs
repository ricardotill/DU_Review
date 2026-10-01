namespace ScoutingAutomation.Domain.Exceptions;

/// <summary>
/// The Slack file is already registered: the same event was handled twice at the same time.
/// </summary>
public class DuplicateSubmissionException : Exception
{
    public DuplicateSubmissionException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
