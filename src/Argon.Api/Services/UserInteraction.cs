namespace Argon.Api.Services;

using ActualLab.Rpc.Infrastructure;
using AutoMapper;
using Grains.Interfaces;
using Contracts;
using Features.Jwt;

public class UserInteraction(IGrainFactory grainFactory, IFusionContext fusionContext, IMapper mapper) : IUserInteraction
{
    public async Task<UserResponse> GetMe()
    {
        var userData = await fusionContext.GetUserDataAsync();
        var user = await grainFactory.GetGrain<IUserManager>(userData.id).GetUser();
        return mapper.Map<UserResponse>(user);
    }

    public async Task<ServerDefinition> CreateServer(CreateServerRequest request)
    {
        var userData = await fusionContext.GetUserDataAsync();
        var serverId = Guid.NewGuid();
        var server   = await grainFactory
           .GetGrain<IServerGrain>(serverId)
           .CreateServer(new ServerInput(request.Name, request.Description, request.AvatarUrl), userData.id);
        return mapper.Map<ServerDefinition>(server);
    }

    public async Task<List<ServerDefinition>> GetServers()
    {
        var userData = await fusionContext.GetUserDataAsync();
        var servers = await grainFactory.GetGrain<IUserManager>(userData.id).GetMyServers();
        return servers.Select(mapper.Map<ServerDefinition>).ToList();
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