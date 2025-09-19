namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

[Alias("Argon.Grains.Interfaces.IChannelGrain")]
public interface IChannelGrain : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<Either<string, JoinToChannelError>> Join();

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("GetChannel")]
    Task<ChannelEntity> GetChannel();

    [Alias("UpdateChannel")]
    Task<ChannelEntity> UpdateChannel(ChannelInput input);

    [Alias(nameof(SendMessage))]
    Task<ulong> SendMessage(string text, List<IMessageEntity> entities, ulong? replyTo);

    [Alias(nameof(GetMessages))]
    Task<List<ArgonMessageEntity>> GetMessages(int count, ulong offset);

    [Alias(nameof(QueryMessages))]
    Task<List<ArgonMessageEntity>> QueryMessages(ulong? @from, int limit);

    [Alias("GetMembers")]
    Task<List<RealtimeChannelUser>> GetMembers();

    [OneWay, Alias("ClearChannel")]
    Task ClearChannel();


    [OneWay, Alias("OnTypingEmit")]
    ValueTask OnTypingEmit();
    [OneWay, Alias("OnTypingStopEmit")]
    ValueTask OnTypingStopEmit();


    [Alias("KickMemberFromChannel")]
    Task<bool> KickMemberFromChannel(Guid memberId);
}


public sealed record ChannelInput(
    string Name,
    string? Description,
    ChannelType ChannelType);