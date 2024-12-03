namespace Argon;

using Streaming.Events;

[TsInterface]
public interface IServerInteraction : IArgonService
{
    Task<CreateChannelResponse> CreateChannel(CreateChannelRequest request);
    Task                        DeleteChannel(DeleteChannelRequest request);
    Task<ChannelJoinResponse>   JoinToVoiceChannel(JoinToVoiceChannelRequest request);
}