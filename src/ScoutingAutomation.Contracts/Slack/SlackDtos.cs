using System.Text.Json.Serialization;

namespace ScoutingAutomation.Contracts.Slack;

public class SlackEventEnvelopeDto
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("team_id")]
    public string? TeamId { get; set; }

    [JsonPropertyName("event")]
    public SlackEventItemDto? Event { get; set; }
}

public class SlackEventItemDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("ts")]
    public string? Ts { get; set; }

    [JsonPropertyName("reaction")]
    public string? Reaction { get; set; }

    [JsonPropertyName("item_user")]
    public string? ItemUser { get; set; }

    [JsonPropertyName("item")]
    public SlackReactionTargetDto? Item { get; set; }

    [JsonPropertyName("files")]
    public List<SlackFileItemDto>? Files { get; set; }

    // Fields below come with "message_changed" and "message_deleted" events

    /// <summary>The message as it is after an edit.</summary>
    [JsonPropertyName("message")]
    public SlackEventItemDto? Message { get; set; }

    /// <summary>The message as it was before an edit or deletion.</summary>
    [JsonPropertyName("previous_message")]
    public SlackEventItemDto? PreviousMessage { get; set; }

    [JsonPropertyName("deleted_ts")]
    public string? DeletedTs { get; set; }

    // Fields below come with messages read from the history API

    [JsonPropertyName("bot_id")]
    public string? BotId { get; set; }

    [JsonPropertyName("thread_ts")]
    public string? ThreadTs { get; set; }

    [JsonPropertyName("reply_count")]
    public int ReplyCount { get; set; }

    [JsonPropertyName("reactions")]
    public List<SlackMessageReactionDto>? Reactions { get; set; }
}

/// <summary>
/// A reaction as it currently stands on a message: the emoji and everyone who placed it.
/// </summary>
public class SlackMessageReactionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("users")]
    public List<string>? Users { get; set; }
}

/// <summary>
/// Response of conversations.history and conversations.replies.
/// </summary>
public class SlackHistoryResponseDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("messages")]
    public List<SlackEventItemDto>? Messages { get; set; }

    [JsonPropertyName("response_metadata")]
    public SlackResponseMetadataDto? ResponseMetadata { get; set; }
}

/// <summary>
/// Response of reactions.get: the message with its current reactions.
/// </summary>
public class SlackReactionsResponseDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("message")]
    public SlackEventItemDto? Message { get; set; }
}

/// <summary>Response of auth.test: who the bot is and in which workspace.</summary>
public class SlackAuthTestResponseDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>The workspace's web address, e.g. "https://scouting.slack.com/".</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("team_id")]
    public string? TeamId { get; set; }
}

/// <summary>Response of users.info.</summary>
public class SlackUserInfoResponseDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("user")]
    public SlackUserDto? User { get; set; }
}

public class SlackUserDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("real_name")]
    public string? RealName { get; set; }

    [JsonPropertyName("profile")]
    public SlackUserProfileDto? Profile { get; set; }
}

public class SlackUserProfileDto
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("real_name")]
    public string? RealName { get; set; }
}

public class SlackResponseMetadataDto
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }
}

public class SlackReactionTargetDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("ts")]
    public string? Ts { get; set; }
}

public class SlackFileItemDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("mimetype")]
    public string? Mimetype { get; set; }

    [JsonPropertyName("filetype")]
    public string? Filetype { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url_private")]
    public string? UrlPrivate { get; set; }

    [JsonPropertyName("url_private_download")]
    public string? UrlPrivateDownload { get; set; }

    /// <summary>"tombstone" when the file was deleted but the message still shows it.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonIgnore]
    public bool IsDeleted => Mode == "tombstone";
}
