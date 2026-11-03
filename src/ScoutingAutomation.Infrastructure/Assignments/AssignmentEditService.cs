using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Assignments;

/// <summary>What the jury changes about an assignment in the app.</summary>
public sealed record AssignmentEdit(string? Title, string? Description, int StandardPoints, int AwesomePoints);

/// <summary>
/// Lets the jury change an assignment's title, description and points, and add pictures to it. An edited assignment
/// keeps its values when the app starts again, until it is set back to the points file. The scores follow by
/// themselves: they are computed from the assignment's points.
/// </summary>
public sealed class AssignmentEditService
{
    /// <summary>The largest picture, in bytes.</summary>
    public const long MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>The most pictures of one assignment.</summary>
    public const int MaxImagesPerAssignment = 20;

    /// <summary>The most points an assignment can give; more is a typing error.</summary>
    public const int MaxPoints = 1000;

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp"
    };

    private readonly IAssignmentRepository _assignments;
    private readonly IAssignmentImageRepository _images;
    private readonly IFileStorageService _storage;
    private readonly ISubmissionChangeNotifier _notifier;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AssignmentEditService> _logger;

    public AssignmentEditService(
        IAssignmentRepository assignments,
        IAssignmentImageRepository images,
        IFileStorageService storage,
        ISubmissionChangeNotifier notifier,
        IOptions<ScoutingSettings> options,
        TimeProvider time,
        ILogger<AssignmentEditService> logger)
    {
        _assignments = assignments;
        _images = images;
        _storage = storage;
        _notifier = notifier;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The accepted picture extensions, for the file picker.</summary>
    public static string AcceptedImageTypes => string.Join(",", ImageTypes.Keys);

    /// <summary>Why the change cannot be saved, or null when it can.</summary>
    public static string? Validate(AssignmentEdit edit)
    {
        if (edit.StandardPoints is < 0 or > MaxPoints || edit.AwesomePoints is < 0 or > MaxPoints) return $"Punten liggen tussen 0 en {MaxPoints}.";
        if (edit.Title?.Trim().Length > ScoutingDbContext.MaxAssignmentTitleLength) return $"De titel is te lang: maximaal {ScoutingDbContext.MaxAssignmentTitleLength} tekens.";
        if (edit.Description?.Trim().Length > ScoutingDbContext.MaxAssignmentDescriptionLength) return $"De beschrijving is te lang: maximaal {ScoutingDbContext.MaxAssignmentDescriptionLength} tekens.";
        return null;
    }

    /// <returns>Why it was not saved, or null when it was.</returns>
    public async Task<string?> SaveAsync(string code, AssignmentEdit edit, string? editedBy, CancellationToken ct = default)
    {
        if (Validate(edit) is { } problem) return problem;

        var assignment = await _assignments.GetByCodeAsync(code, ct);
        if (assignment == null) return $"Opdracht {code} bestaat niet.";

        assignment.Title = Blank(edit.Title);
        assignment.Description = Blank(edit.Description);
        assignment.StandardPoints = edit.StandardPoints;
        assignment.AwesomePoints = edit.AwesomePoints;
        assignment.EditedAt = _time.GetUtcNow();
        await _assignments.UpsertAsync(assignment, ct);

        _logger.LogWarning("Assignment {Code} edited in the app by {User}: {Standard}/{Awesome} points, title '{Title}'",
            code, editedBy ?? "?", edit.StandardPoints, edit.AwesomePoints, assignment.Title);
        _notifier.NotifyChanged();
        return null;
    }

    /// <summary>Puts the title, description and points from the points file back.</summary>
    /// <returns>Why it could not, or null when it did.</returns>
    public async Task<string?> ResetAsync(string code, CancellationToken ct = default)
    {
        var assignment = await _assignments.GetByCodeAsync(code, ct);
        if (assignment == null) return $"Opdracht {code} bestaat niet.";

        var file = Path.GetFullPath(_options.Value.AssignmentPointsFile);
        IReadOnlyList<Assignment> configured;
        try
        {
            using var reader = new StreamReader(file);
            configured = AssignmentPointsCsvReader.Read(reader);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the points file {File} to reset assignment {Code}", file, code);
            return "Het puntenbestand kon niet gelezen worden.";
        }

        var original = configured.FirstOrDefault(a => a.Code == code);
        if (original == null) return $"Opdracht {code} staat niet in het puntenbestand.";

        assignment.Title = original.Title;
        assignment.Description = original.Description;
        assignment.StandardPoints = original.StandardPoints;
        assignment.AwesomePoints = original.AwesomePoints;
        assignment.EditedAt = null;
        await _assignments.UpsertAsync(assignment, ct);

        _logger.LogWarning("Assignment {Code} set back to the points file", code);
        _notifier.NotifyChanged();
        return null;
    }

    public Task<IReadOnlyList<AssignmentImage>> GetImagesAsync(string code, CancellationToken ct = default) => _images.GetForAssignmentAsync(code, ct);

    /// <summary>Stores a picture with the assignment, after it.</summary>
    /// <returns>Why it was not added, or null when it was.</returns>
    public async Task<string?> AddImageAsync(string code, string fileName, Stream content, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName);
        if (!ImageTypes.ContainsKey(extension)) return $"{Path.GetFileName(fileName)}: alleen JPG, PNG, GIF of WebP.";

        if (await _assignments.GetByCodeAsync(code, ct) == null) return $"Opdracht {code} bestaat niet.";

        var existing = await _images.GetForAssignmentAsync(code, ct);
        if (existing.Count >= MaxImagesPerAssignment) return $"Een opdracht heeft hoogstens {MaxImagesPerAssignment} afbeeldingen.";

        // Read first, with a limit: the size and the first bytes decide, not what the browser said
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes) return $"{Path.GetFileName(fileName)} is te groot: maximaal {MaxImageBytes / 1024 / 1024} MB.";
            buffer.Write(chunk, 0, read);
        }

        if (!LooksLikeImage(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))) return $"{Path.GetFileName(fileName)} is geen afbeelding.";

        buffer.Position = 0;
        var id = Guid.NewGuid();
        var path = await _storage.SaveAssignmentImageAsync(code, $"{id:N}{extension.ToLowerInvariant()}", buffer, ct);
        try
        {
            await _images.AddAsync(new AssignmentImage
            {
                Id = id,
                AssignmentCode = code,
                FileName = Truncate(Path.GetFileName(fileName), ScoutingDbContext.MaxAssignmentImageNameLength),
                StoragePath = path,
                SortOrder = existing.Count == 0 ? 0 : existing.Max(i => i.SortOrder) + 1,
                UploadedAt = _time.GetUtcNow()
            }, ct);
        }
        catch
        {
            // Not in the database: the file would only take room
            await _storage.DeleteFileAsync(path, CancellationToken.None);
            throw;
        }

        _logger.LogInformation("Added picture {File} to assignment {Code}", fileName, code);
        _notifier.NotifyChanged();
        return null;
    }

    public async Task DeleteImageAsync(Guid id, CancellationToken ct = default)
    {
        var removed = await _images.DeleteAsync(id, ct);
        if (removed == null) return;

        await _storage.DeleteFileAsync(removed.StoragePath, ct);
        _logger.LogInformation("Removed picture {File} from assignment {Code}", removed.FileName, removed.AssignmentCode);
        _notifier.NotifyChanged();
    }

    /// <summary>Moves a picture one place earlier (<paramref name="offset"/> -1) or later (+1).</summary>
    public async Task MoveImageAsync(Guid id, int offset, CancellationToken ct = default)
    {
        var image = await _images.GetAsync(id, ct);
        if (image == null) return;

        var order = (await _images.GetForAssignmentAsync(image.AssignmentCode, ct)).Select(i => i.Id).ToList();
        var from = order.IndexOf(id);
        var to = Math.Clamp(from + offset, 0, order.Count - 1);
        if (from < 0 || from == to) return;

        order.RemoveAt(from);
        order.Insert(to, id);
        await _images.ReorderAsync(image.AssignmentCode, order, ct);
        _notifier.NotifyChanged();
    }

    /// <summary>The type to serve a stored picture as, from its extension; null for anything that is no picture.</summary>
    public static string? ContentTypeOf(string path) => ImageTypes.GetValueOrDefault(Path.GetExtension(path));

    /// <summary>JPEG, PNG, GIF or WebP, by the first bytes.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> data) =>
        data.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })
        || data.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
        || data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)
        || (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8));

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
