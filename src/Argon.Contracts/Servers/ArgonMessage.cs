namespace Argon;

using System.ComponentModel.DataAnnotations.Schema;
using MessagePack.Formatters;

[MessagePackFormatter(typeof(EnumAsStringFormatter<EntityType>))]
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

[TsInterface, MessagePackObject(true), MessagePackFormatter(typeof(MessageEntityFormatter))]
public record MessageEntity
{
    public const int GlobalVersion = 1;

    public EntityType Type    { get; set; }
    public int        Offset  { get; set; }
    public int        Length  { get; set; }
    public int        Version { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageEntityMention : MessageEntity
{
    public required Guid UserId { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageEntityEmail : MessageEntity
{
    public required string Email { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageEntityHashTag : MessageEntity
{
    public required string Hashtag { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageEntityQuote : MessageEntity
{
    public required Guid QuotedUserId { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageEntityUrl : MessageEntity
{
    public required string Domain { get; set; }
    public required string Path { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record Sticker : ArgonEntityWithOwnership
{
    public ulong  MessageId  { get; set; }
    public bool   IsAnimated { get; set; }
    public string Emoji      { get; set; }
    public string FileId     { get; set; }
    [JsonIgnore, TsIgnore, ForeignKey(nameof(MessageId))]
    public ArgonMessage ArgonMessage { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record MessageDocument : ArgonEntityWithOwnership
{
    public ulong  MessageId { get; set; }
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
    public ulong  MessageId { get; set; }
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
    public ulong MessageId { get;  set; }
    public Guid   ServerId  { get; set; }
    public Guid   ChannelId { get; set; }
    public ulong? Reply     { get; set; }

    [MaxLength(2048)]
    public string Text { get; set; }

    [Column(TypeName = "jsonb")]
    public List<MessageEntity> Entities { get; set; } = new();
}

[MessagePackObject(true)]
public sealed record ArgonMessageDto(ulong MessageId, ulong? ReplyId, Guid ChannelId, Guid ServerId, string Text, List<MessageEntity> Entities, long TimeSent, Guid Sender);

public static class ArgonMessageExtensions
{
    public static ArgonMessageDto ToDto(this ArgonMessage msg) => new(msg.MessageId, msg.Reply, msg.ChannelId, msg.ServerId, msg.Text, msg.Entities,
        msg.CreatedAt.ToUnixTimeSeconds(), msg.CreatorId);

    public static List<ArgonMessageDto> ToDto(this List<ArgonMessage> msg) => msg.Select(x => x.ToDto()).ToList();

    public async static Task<ArgonMessageDto>       ToDto(this Task<ArgonMessage> msg) => (await msg).ToDto();
    public async static Task<List<ArgonMessageDto>> ToDto(this Task<List<ArgonMessage>> msg) => (await msg).ToDto();
}