namespace Argon;

using System.ComponentModel.DataAnnotations.Schema;
using ArchetypeModel;

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
    public Guid                             MessageId  { get; set; }
    public EntityType                       Type       { get; set; }
    public int                              Offset     { get; set; }
    public int                              Length     { get; set; }
    public string?                          UrlMask    { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public ArgonMessage ArgonMessage { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Sticker : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }
    public Guid                             MessageId  { get; set; }
    public bool                             IsAnimated { get; set; }
    public string                           Emoji      { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public Guid FileId { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Document : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }
    public Guid                             MessageId  { get; set; }
    public string                           FileName   { get; set; }
    public string                           MimeType   { get; set; }
    public Guid                             FileId     { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public ulong FileSize { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Image : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }
    public Guid                             MessageId  { get; set; }
    public string                           FileName   { get; set; }
    public string                           MimeType   { get; set; }
    public bool                             IsVideo    { get; set; }
    public Guid                             FileId     { get; set; }
    public int                              Width      { get; set; }
    public int                              Height     { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public ulong FileSize { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record ArgonMessage : ArgonEntityWithOwnership, IArchetypeObject
{
    public ICollection<IArchetypeOverwrite> Overwrites { get; }

    public Guid ChannelId      { get; set; }
    public Guid ReplyToMessage { get; set; }

    [MaxLength(1024)]
    public string Text { get;           set; }
    public Document?    Document { get; set; }
    public List<Image>? Image    { get; set; } = new();
    public Sticker?     Sticker  { get; set; }
    public List<Entity> Entities { get; set; } = new();

    public bool IsEmpty() => string.IsNullOrEmpty(Text) && string.IsNullOrWhiteSpace(Text) && Document is null
                             && Image is null && Sticker is null && Entities.Count == 0;
}