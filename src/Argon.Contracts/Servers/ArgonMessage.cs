namespace Argon;

using System.ComponentModel.DataAnnotations.Schema;
using ArchetypeModel;
using Features.Web;

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
    public EntityType Type    { get; set; }
    public int        Offset  { get; set; }
    public int        Length  { get; set; }
    public string?    UrlMask { get; set; }
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