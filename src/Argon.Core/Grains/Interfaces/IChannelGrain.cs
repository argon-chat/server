namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

[Alias("Argon.Grains.Interfaces.IChannelGrain")]
public interface IChannelGrain : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<Either<string, JoinToChannelError>> Join();

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("UpdateChannel")]
    Task<ChannelEntity> UpdateChannel(ChannelInput input);

    [Alias(nameof(SendMessage))]
    Task<long> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo);

    [Alias(nameof(QueryMessages))]
    Task<List<ArgonMessageEntity>> QueryMessages(long? @from, int limit);

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

    [Alias("BeginRecord")]
    Task<bool> BeginRecord(CancellationToken ct = default);
    [Alias("StopRecord")]
    Task<bool> StopRecord(CancellationToken ct = default);
}


public sealed record ChannelInput(
    string Name,
    string? Description,
    ChannelType ChannelType);