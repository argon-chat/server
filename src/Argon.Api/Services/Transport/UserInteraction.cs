namespace Argon.Services;

using Features.MediaStorage;

public class UserInteraction(IGrainFactory grainFactory, IContentDeliveryNetwork cdn) : IUserInteraction
{
    public async Task<User> GetMe()
    {
        var userData = this.GetUser();
        return await grainFactory.GetGrain<IUserGrain>(userData.id).GetMe();
    }

    public async Task<Server> CreateServer(CreateServerRequest request)
    {
        var userData = this.GetUser();
        var serverId = Guid.NewGuid();
        var server   = await grainFactory
           .GetGrain<IServerGrain>(serverId)
           .CreateServer(new ServerInput(request.Name, request.Description, request.AvatarFileId), userData.id);
        return server.Value;
    }

    public async Task<List<Server>> GetServers()
    {
        var userData = this.GetUser();
        var servers  = await grainFactory.GetGrain<IUserGrain>(userData.id).GetMyServers();
        return servers.Select(RegenerateAvatarUrl).ToList();
    }

    [AllowAnonymous]
    public async Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input)
    {
        var clientName = this.GetClientName();
        var ipAddress  = this.GetIpAddress();
        var region     = this.GetRegion();
        var hostName   = this.GetHostName();

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        var result = await grainFactory
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .Authorize(input, connInfo);
        return result;
    }

    [AllowAnonymous]
    public async Task<Maybe<RegistrationError>> Registration(NewUserCredentialsInput input)
    {
        var clientName = this.GetClientName();
        var ipAddress  = this.GetIpAddress();
        var region     = this.GetRegion();
        var hostName   = this.GetHostName();

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        var result = await grainFactory
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .Register(input, connInfo);
        return result;
    }

    private Server RegenerateAvatarUrl(Server s)
    {
        if (string.IsNullOrEmpty(s.AvatarFileId))
            return RegenerateUsersAvatars(s);
        return RegenerateUsersAvatars(s) with
        {
            AvatarFileId = cdn.GenerateAssetUrl(StorageNameSpace.ForServer(s.Id), AssetId.FromFileId(s.AvatarFileId!))
        };
    }


    private Server RegenerateUsersAvatars(Server s)
    {
        if (s.Users.Count == 0)
            return s;

        foreach (var user in s.Users.Where(x => x.User is { AvatarFileId: not null }))
        {
            user.User.AvatarFileId = cdn.GenerateAssetUrl(StorageNameSpace.ForUser(user.Id), AssetId.FromFileId(s.AvatarFileId!));
        }

        return s;
    }
}