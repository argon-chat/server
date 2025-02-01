namespace Argon.Grains.Interfaces;

using Argon.Servers;
using Orleans.Concurrency;

[Alias("Argon.Grains.Interfaces.IChannelGrain")]
public interface IChannelGrain : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<Either<string, JoinToChannelError>> Join(Guid userId, Guid sessionId);

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("GetChannel")]
    Task<Channel> GetChannel();

    [Alias("UpdateChannel")]
    Task<Channel> UpdateChannel(ChannelInput input);

    [Alias("GetMembers")]
    Task<List<RealtimeChannelUser>> GetMembers();

    [OneWay, Alias("ClearChannel")]
    Task ClearChannel();
}

[MessagePackObject(true)]
public sealed record ChannelInput(
    string Name,
    ChannelEntitlementOverwrite AccessLevel,
    string? Description,
    ChannelType ChannelType);