// namespace Argon.Api.Services;
//
// using Contracts;
// using Grains.Interfaces;
//
// public class UserInteractionService(
//     IGrainFactory grainFactory
// ) : IUserInteraction
// {
//     private readonly IUserManager userManager = grainFactory.GetGrain<IUserManager>(username);
//
//     public async Task<UserResponse> GetMe()
//     {
//         return await userManager.Get();
//     }
//
//     public async Task<ServerResponse> CreateServer(CreateServerRequest request)
//     {
//         return await userManager.CreateServer(request.Name, request.Description);
//     }
//
//     public async Task<List<ServerResponse>> GetServers()
//     {
//         return (await userManager.GetServers())
//             .Select(x => (ServerResponse)x)
//             .ToList();
//     }
//
//     public async Task<List<ServerDetailsResponse>> GetServerDetails(ServerDetailsRequest request)
//     {
//         return (await userManager.GetServerChannels(request.ServerId))
//             .Select(x => (ServerDetailsResponse)x)
//             .ToList();
//     }
//
//     public async Task<ChannelJoinResponse> JoinChannel(ChannelJoinRequest request)
//     {
//         return new ChannelJoinResponse((await userManager.JoinChannel(request.ServerId, request.ChannelId)).value);
//     }
// }
//

