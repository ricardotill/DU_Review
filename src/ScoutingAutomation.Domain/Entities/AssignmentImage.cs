namespace ScoutingAutomation.Domain.Entities;

/// <summary>A picture that belongs to an assignment, e.g. a photo the groups have to recreate. Groups see it once the assignment is open.</summary>
public class AssignmentImage : IEditionScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Each edition has its own assignments, and so its own pictures.</summary>
    public Guid EditionId { get; set; }

    /// <summary>The normalised code of the assignment, see <see cref="Models.AssignmentCode"/>.</summary>
    public string AssignmentCode { get; set; } = string.Empty;

    /// <summary>The name of the uploaded file, for the download.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Where the file is stored, like a submission's: under "_opdrachten" in the edition's folder.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>The position among the pictures of the assignment, lowest first.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
