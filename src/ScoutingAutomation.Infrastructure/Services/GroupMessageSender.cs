using Microsoft.Extensions.Logging;
using ScoutingAutomation.Domain.Interfaces;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Posts a message from the jury, as the bot, in the channels of the chosen groups.
/// Text-only messages are no submissions, so the bot ignores them when they come back as events.
/// </summary>
public sealed class GroupMessageSender
{
    public const int MaxLength = RuntimeSettingsService.MaxSlackTextLength;

    /// <summary>The largest picture that can be sent, in bytes.</summary>
    public const long MaxPictureBytes = 20 * 1024 * 1024;

    /// <summary>The longest file name sent to Slack; a longer one is cut short.</summary>
    public const int MaxFileNameLength = 100;

    // ISO media brands of HEIC/HEIF (iPhone photos) and AVIF
    private static readonly string[] HeifBrands = { "heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1", "avif" };

    private readonly IGroupRepository _groups;
    private readonly ISlackClient _slack;
    private readonly ILogger<GroupMessageSender> _logger;

    public GroupMessageSender(IGroupRepository groups, ISlackClient slack, ILogger<GroupMessageSender> logger)
    {
        _groups = groups;
        _slack = slack;
        _logger = logger;
    }

    /// <summary>Why the message cannot be posted, or null when it can. With a picture, the text may be empty.</summary>
    public static string? Validate(string? text, MessagePicture? picture = null)
    {
        if (string.IsNullOrWhiteSpace(text) && picture == null) return "Het bericht is leeg.";
        if (text?.Length > MaxLength) return $"Het bericht is te lang: maximaal {MaxLength} tekens.";
        if (picture != null && picture.Content.Length == 0) return "De foto is leeg.";
        if (picture?.Content.Length > MaxPictureBytes) return $"De foto is te groot: maximaal {MaxPictureBytes / 1024 / 1024} MB.";
        if (picture != null && !IsPicture(picture.Content)) return "Kies een afbeelding.";
        return null;
    }

    /// <summary>
    /// Whether the bytes are a JPEG, PNG, GIF, WebP, HEIC or AVIF picture.
    /// The browser's content type is only a claim, so the bytes themselves decide.
    /// </summary>
    public static bool IsPicture(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return true;
        if (content.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A })) return true;
        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8)) return true;
        if (content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8)) return true;
        if (content.Length >= 12 && content[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(content[8..12]);
            return HeifBrands.Contains(brand);
        }
        return false;
    }

    /// <summary>
    /// The name to show in Slack: no folders or control characters, at most <see cref="MaxFileNameLength"/> characters,
    /// and "foto" plus the extension when nothing is left.
    /// </summary>
    public static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();

        var stem = Path.GetFileNameWithoutExtension(name).Trim();
        var extension = Path.GetExtension(name);
        // A "extension" this long is part of the name
        if (extension.Length > 10)
        {
            stem = name;
            extension = "";
        }

        if (stem.Length == 0) return "foto" + extension;
        if (stem.Length + extension.Length > MaxFileNameLength) stem = stem[..(MaxFileNameLength - extension.Length)].TrimEnd();
        return stem + extension;
    }

    /// <summary>
    /// Posts <paramref name="text"/> (Slack mrkdwn) once in the channel of each chosen group,
    /// with <paramref name="picture"/> under it when given.
    /// </summary>
    public async Task<IReadOnlyList<AnnouncementPostResult>> PostAsync(string? text, IReadOnlyCollection<Guid> groupIds,
        MessagePicture? picture = null, CancellationToken ct = default)
    {
        if (Validate(text, picture) is { } problem) throw new ArgumentException(problem, nameof(text));

        var groups = await _groups.GetAllAsync(ct);
        var results = new List<AnnouncementPostResult>();
        foreach (var group in groups.Where(g => groupIds.Contains(g.Id) && !string.IsNullOrWhiteSpace(g.SlackChannelId)))
        {
            // Slack has no way to share one upload in several channels with an error per channel, so each gets its own
            var error = picture == null
                ? await _slack.PostMessageAsync(group.SlackChannelId, text!, ct)
                : await _slack.PostFileAsync(group.SlackChannelId, picture.Content, SafeFileName(picture.FileName), text, ct);
            if (error == null)
            {
                _logger.LogInformation("Posted a jury message for {Group} in {Channel}", group.Name, group.SlackChannelId);
            }
            else
            {
                _logger.LogWarning("Could not post a jury message for {Group} in {Channel}: {Error}", group.Name, group.SlackChannelId, error);
            }

            results.Add(new AnnouncementPostResult(group.Name, group.SlackChannelId, Posted: error == null, Skipped: false, error));
        }

        return results;
    }
}

/// <summary>A picture sent along with a jury message.</summary>
public sealed record MessagePicture(string FileName, byte[] Content);
