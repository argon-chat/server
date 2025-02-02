namespace Argon;

using ArchetypeModel;
using Streaming;
using Servers;

public enum EntityType : ushort
{
    Hashtag,
    Mention,
    Email,
    PhoneNumber,
    Url,
    Monospace,
    Quote,
    Spoiler,
    Strikethrough,
    Bold,
    Italic,
    Underline,
}

[TsInterface, MessagePackObject(true)]
public record Entity : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }
    public EntityType                       Type       { get; set; }
    public int                              Offset     { get; set; }
    public int                              Length     { get; set; }
    public string?                          UrlMask    { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Sticker : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }
    public Guid                             FileId     { get; set; }
    public bool                             IsAnimated { get; set; }
    public string                           Emoji      { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Document : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }
    public ulong                            FileSize   { get; set; }
    public string                           FileName   { get; set; }
    public string                           MimeType   { get; set; }
    public Guid                             FileId     { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Image : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }
    public ulong                            FileSize   { get; set; }
    public string                           FileName   { get; set; }
    public string                           MimeType   { get; set; }
    public bool                             IsVideo    { get; set; }
    public Guid                             FileId     { get; set; }
    public int                              Width      { get; set; }
    public int                              Height     { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record ArgonMessage : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }

    public Guid    MessageId      { get; set; }
    public Guid    ChannelId      { get; set; }
    public Guid    ReplyToMessage { get; set; }
    public string? UserName       { get; set; }

    [MaxLength(1024)]
    public string Text { get;           set; }
    public Document?    Document { get; set; }
    public List<Image>? Image    { get; set; }
    public Sticker?     Sticker  { get; set; }
    public List<Entity> Entities { get; set; } = new();

    public bool IsEmpty() => string.IsNullOrEmpty(Text) && string.IsNullOrWhiteSpace(Text) && Document is null
                             && Image is null && Sticker is null && Entities.Count == 0;
}

[TsInterface, MessagePackObject(true)]
public record Channel : ArgonEntityWithOwnership, IArchetypeObject
{
    public ChannelType ChannelType { get; set; }
    public Guid        ServerId    { get; set; }
    [IgnoreMember, JsonIgnore, TsIgnore]
    public virtual Server Server { get; set; }


    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Description { get; set; } = null;

    public virtual ICollection<ChannelEntitlementOverwrite> EntitlementOverwrites { get; set; }
        = new List<ChannelEntitlementOverwrite>();
    public ICollection<IArchetypeOverwrite> Overwrites => EntitlementOverwrites.OfType<IArchetypeOverwrite>().ToList();
}

[TsInterface, MessagePackObject(true)]
public record RealtimeChannel
{
    public Channel Channel { get; set; }

    public List<RealtimeChannelUser> Users { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record RealtimeChannelUser
{
    public Guid UserId { get; set; }

    public ChannelMemberState State { get; set; }
}

[Flags]
public enum ChannelMemberState
{
    NONE                       = 0,
    MUTED                      = 1 << 1,
    MUTED_BY_SERVER            = 1 << 2,
    MUTED_HEADPHONES           = 1 << 3,
    MUTED_HEADPHONES_BY_SERVER = 1 << 4,
    STREAMING                  = 1 << 5
}

public enum JoinToChannelError
{
    NONE,
    CHANNEL_IS_NOT_VOICE
}