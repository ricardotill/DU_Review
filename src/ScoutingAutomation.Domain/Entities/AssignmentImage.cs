namespace ScoutingAutomation.Domain.Entities;

/// <summary>A picture that belongs to an assignment, e.g. a photo the groups have to recreate. Groups see it once the assignment is open.</summary>
public class AssignmentImage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The normalised code of the assignment, see <see cref="Models.AssignmentCode"/>.</summary>
    public string AssignmentCode { get; set; } = string.Empty;

    /// <summary>The name of the uploaded file, for the download.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Relative to the storage root.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>The position among the pictures of the assignment, lowest first.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
