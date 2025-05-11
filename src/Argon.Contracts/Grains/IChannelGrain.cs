namespace Argon.Grains.Interfaces;

using Argon.Servers;
using Orleans.Concurrency;
using System.Threading.Tasks;

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

    [Alias(nameof(SendMessage))]
    Task SendMessage(Guid senderId, string text, List<MessageEntity> entities, ulong? replyTo);

    [Alias(nameof(GetMessages))]
    Task<List<ArgonMessage>> GetMessages(int count, int offset);

    [Alias("GetMembers")]
    Task<List<RealtimeChannelUser>> GetMembers();

    [OneWay, Alias("ClearChannel")]
    Task ClearChannel();


    [OneWay, Alias("OnTypingEmit")]
    ValueTask OnTypingEmit(Guid serverId, Guid userId);
    [OneWay, Alias("OnTypingStopEmit")]
    ValueTask OnTypingStopEmit(Guid serverId, Guid userId);
}

[MessagePackObject(true)]
public sealed record ChannelInput(
    string Name,
    ChannelEntitlementOverwrite AccessLevel,
    string? Description,
    ChannelType ChannelType);