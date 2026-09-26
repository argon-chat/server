namespace Argon.Entities;

/// <summary>Where a crossposted copy came from, as it was at publish time. Stored as jsonb on the copy.</summary>
public sealed record MessageCrosspost
{
    public Guid    SourceSpaceId           { get; init; }
    public Guid    SourceChannelId         { get; init; }
    public long    SourceMessageId         { get; init; }
    public string  SourceSpaceName         { get; init; } = string.Empty;
    public string  SourceChannelName       { get; init; } = string.Empty;
    public string? SourceSpaceAvatarFileId { get; init; }
    public bool    HideAuthor              { get; init; }

    public static CrosspostInfo? ToDto(MessageCrosspost? self)
        => self is null
            ? null
            : new CrosspostInfo(self.SourceSpaceId, self.SourceChannelId, self.SourceMessageId,
                self.SourceSpaceName, self.SourceChannelName, self.SourceSpaceAvatarFileId, self.HideAuthor);
}
