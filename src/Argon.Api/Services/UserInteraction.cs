namespace Argon.Api.Services;

using ActualLab.Rpc.Infrastructure;
using Grains.Interfaces;
using Contracts;
using Contracts.Models;
using Features.Jwt;

public class UserInteraction(IGrainFactory grainFactory, IFusionContext fusionContext) : IUserInteraction
{
    public async Task<User> GetMe()
    {
        var userData = await fusionContext.GetUserDataAsync();
        return await grainFactory.GetGrain<IUserGrain>(userData.id).GetUser();
    }

    public async Task<Server> CreateServer(CreateServerRequest request)
    {
        var userData = await fusionContext.GetUserDataAsync();
        var serverId = Guid.NewGuid();
        var server   = await grainFactory
           .GetGrain<IServerGrain>(serverId)
           .CreateServer(new ServerInput(request.Name, request.Description, request.AvatarFileId), userData.id);
        return server.Value;
    }

    public async Task<List<Server>> GetServers()
    {
        var userData = await fusionContext.GetUserDataAsync();
        var servers = await grainFactory.GetGrain<IUserGrain>(userData.id).GetMyServers();
        return servers.ToList();
    }
}

public class FusionContext(IGrainFactory grainFactory) : IFusionContext
{
    public ValueTask<TokenUserData> GetUserDataAsync()
        => grainFactory.GetGrain<IFusionSessionGrain>(RpcInboundContext.GetCurrent().Peer.Id).GetTokenUserData();
}

public interface IFusionContext
{
    ValueTask<TokenUserData> GetUserDataAsync();
}