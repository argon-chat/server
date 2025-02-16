namespace Argon;

using System.ComponentModel.DataAnnotations.Schema;
using ArchetypeModel;

public enum EntityType : ushort
{
    Hashtag,
    Mention,
    Email,
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
public record MessageEntity
{
    public ulong MessageId { get; set; }
    public Guid  ChannelId { get; set; }


    public EntityType Type    { get; set; }
    public int        Offset  { get; set; }
    public int        Length  { get; set; }
    public string?    UrlMask { get; set; }

    [JsonIgnore, TsIgnore]
    public ArgonMessage ArgonMessage { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Sticker : ArgonEntityWithOwnership
{
    public Guid   MessageId  { get; set; }
    public bool   IsAnimated { get; set; }
    public string Emoji      { get; set; }
    public string FileId     { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public ArgonMessage ArgonMessage { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageDocument : ArgonEntityWithOwnership
{
    public Guid   MessageId { get; set; }
    public string FileName  { get; set; }
    public string MimeType  { get; set; }
    public ulong  FileSize  { get; set; }
    public string FileId    { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public ArgonMessage ArgonMessage { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageImage : ArgonEntityWithOwnership
{
    public Guid   MessageId { get; set; }
    public string FileName  { get; set; }
    public string MimeType  { get; set; }
    public bool   IsVideo   { get; set; }
    public string FileId    { get; set; }
    public int    Width     { get; set; }
    public int    Height    { get; set; }
    public ulong  FileSize  { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public ArgonMessage ArgonMessage { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record ArgonMessage : ArgonEntityWithOwnershipNoKey
{
    [Required, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public uint  MessageId { get; set; }
    public Guid  ServerId  { get; set; }
    public Guid  ChannelId { get; set; }
    public uint? Reply     { get; set; }

    [Column(TypeName = "jsonb")]
    public List<MessageEntity> Entities { get; set; } = new();
}