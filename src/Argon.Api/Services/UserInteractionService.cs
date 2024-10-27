namespace Argon.Api.Services;

using Contracts;
using Grains.Interfaces;

public class UserInteractionService(
    string username, // TODO to be injected
    IGrainFactory grainFactory
) : IUserInteraction
{
    public async Task<UserResponse> GetMe() => await grainFactory.GetGrain<IUserManager>(username).Get();

    public async Task<ServerResponse> CreateServer(CreateServerRequest request) =>
        await grainFactory
            .GetGrain<IUserManager>(username)
            .CreateServer(request.Name, request.Description);

    public async Task<List<ServerResponse>> GetServers() =>
        (await grainFactory
            .GetGrain<IUserManager>(username)
            .GetServers())
        .Select(x => (ServerResponse)x)
        .ToList();

    public async Task<List<ServerDetailsResponse>> GetServerDetails(ServerDetailsRequest request) =>
        (await grainFactory.GetGrain<IUserManager>(username).GetServerChannels(request.ServerId))
        .Select(x => (ServerDetailsResponse)x)
        .ToList();

    public async Task<ChannelJoinResponse> JoinChannel(ChannelJoinRequest request) =>
        new((await grainFactory.GetGrain<IUserManager>(username)
            .JoinChannel(request.ServerId, request.ChannelId)).value);
}