namespace Argon.Grains.Interfaces;

using Argon.Servers;
using Orleans.Concurrency;
using System.Threading.Tasks;

[Alias("Argon.Grains.Interfaces.IChannelGrain")]
public interface IChannelGrain : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<Either<string, JoinToChannelError>> Join();

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("GetChannel")]
    Task<Channel> GetChannel();

    [Alias("UpdateChannel")]
    Task<Channel> UpdateChannel(ChannelInput input);

    [Alias(nameof(SendMessage))]
    Task SendMessage(string text, List<MessageEntity> entities, ulong? replyTo);

    [Alias(nameof(GetMessages))]
    Task<List<ArgonMessage>> GetMessages(int count, int offset);

    [Alias(nameof(QueryMessages))]
    Task<List<ArgonMessage>> QueryMessages(ulong? @from, int limit);

    [Alias("GetMembers")]
    Task<List<RealtimeChannelUser>> GetMembers();

    [OneWay, Alias("ClearChannel")]
    Task ClearChannel();


    [OneWay, Alias("OnTypingEmit")]
    ValueTask OnTypingEmit();
    [OneWay, Alias("OnTypingStopEmit")]
    ValueTask OnTypingStopEmit();
}

[MessagePackObject(true)]
public sealed record ChannelInput(
    string Name,
    ChannelEntitlementOverwrite AccessLevel,
    string? Description,
    ChannelType ChannelType);