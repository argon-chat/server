namespace Argon.Api.Grains.Persistence.States;

using Contracts;
using MemoryPack;

[GenerateSerializer]
[Serializable]
[MemoryPackable]
[Alias(nameof(ChannelStorage))]
public sealed partial record ChannelStorage
{
    [Id(0)] public Guid Id { get; set; } = Guid.Empty;
    [Id(1)] public string Name { get; set; } = string.Empty;
    [Id(2)] public string Description { get; set; } = string.Empty;
    [Id(3)] public Guid CreatedBy { get; set; } = Guid.Empty;
    [Id(4)] public ChannelType ChannelType { get; set; } = ChannelType.Text;
    [Id(5)] public ServerRole AccessLevel { get; set; } = ServerRole.User;
    [Id(6)] public DateTime CreatedAt { get; } = DateTime.UtcNow;
    [Id(7)] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static implicit operator ServerDetailsResponse(ChannelStorage channelStorage)
    {
        return new ServerDetailsResponse(
            channelStorage.Id,
            channelStorage.Name,
            channelStorage.Description,
            channelStorage.CreatedBy.ToString("N"),
            channelStorage.ChannelType.ToString(),
            channelStorage.AccessLevel.ToString(),
            channelStorage.CreatedAt,
            channelStorage.UpdatedAt
        );
    }
}