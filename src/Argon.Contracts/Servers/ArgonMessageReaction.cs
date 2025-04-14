namespace Argon;

[TsInterface, MessagePackObject(true)]
public record ArgonMessageReaction : ArgonEntityWithOwnership
{
    [Required]
    public Guid ServerId { get; set; }

    [Required]
    public Guid ChannelId { get; set; }

    [Required]
    public ulong MessageId { get; set; }

    [Required]
    public Guid UserId { get; set; }

    [Required, MaxLength(32)]
    public string Reaction { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}