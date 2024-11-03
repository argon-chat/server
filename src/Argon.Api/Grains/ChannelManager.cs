// namespace Argon.Api.Grains;
//
// using Interfaces;
// using Persistence.States;
// using Sfu;
//
// public class ChannelManager(
//     [PersistentState("channels", "OrleansStorage")]
//     IPersistentState<ChannelStorage> channelStore,
//     IGrainFactory grainFactory,
//     IArgonSelectiveForwardingUnit sfu
// ) : Grain, IChannelManager
// {
//     public async Task<ChannelStorage> CreateChannel(ChannelStorage channel)
//     {
//         if (channelStore.State.Id != Guid.Empty) throw new Exception("Channel already exists");
//
//         channelStore.State = channel;
//         await channelStore.WriteStateAsync();
//         return await GetChannel();
//     }
//
//     public async Task<ChannelStorage> GetChannel()
//     {
//         await channelStore.ReadStateAsync();
//         return channelStore.State;
//     }
//
//     public async Task<RealtimeToken> JoinLink(Guid userId, Guid serverId)
//     {
//         return await sfu.IssueAuthorizationTokenAsync(new ArgonUserId(userId),
//             new ArgonChannelId(new ArgonServerId(serverId), this.GetPrimaryKey()),
//             SfuPermission.DefaultUser); // TODO: permissions and flags
//     }
//
//     public async Task<ChannelStorage> UpdateChannel(ChannelStorage channel)
//     {
//         channelStore.State = channel;
//         await channelStore.WriteStateAsync();
//         return await GetChannel();
//     }
// }

