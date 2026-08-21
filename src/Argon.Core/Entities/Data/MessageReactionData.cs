namespace Argon.Entities;

public sealed record MessageReactionData
{
    public required string Emoji     { get; init; }
    public          Guid?  CustomEmojiId { get; init; }
    public          List<Guid> UserIds { get; set; } = [];
}
