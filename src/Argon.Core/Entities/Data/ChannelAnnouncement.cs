namespace Argon.Entities;

/// <summary>How an announcement channel presents its posts; null on the entity means these defaults.</summary>
public sealed record ChannelAnnouncement
{
    public bool Reactions   { get; init; } = true;
    public bool PostAsSpace { get; init; }
    public bool ShowAuthor  { get; init; } = true;

    /// <summary>
    /// The SendMessages deny on everyone was added by creating the channel as an announcement channel
    /// or converting it to one, not by the owner; only then does converting back to text lift it.
    /// </summary>
    public bool AddedSendDeny { get; init; }

    public static readonly ChannelAnnouncement Default = new();

    /// <summary>What the channel stores, or the defaults.</summary>
    public static ChannelAnnouncement Of(ChannelEntity channel)
        => channel.Announcement ?? Default;

    /// <summary>Null unless the channel is an announcement channel; a text channel keeps its values for when it converts back.</summary>
    public static AnnouncementSettings? ToDto(ChannelEntity channel)
        => channel.ChannelType == ChannelType.Announcement
            ? ToDto(Of(channel))
            : null;

    public static AnnouncementSettings ToDto(ChannelAnnouncement self)
        => new(self.Reactions, self.PostAsSpace, self.ShowAuthor);
}
