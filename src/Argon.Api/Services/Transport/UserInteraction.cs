namespace Argon.Services;

using Shared.Servers;

public class UserInteraction(IGrainFactory grainFactory) : IUserInteraction
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
        return servers;
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
    public async Task<Either<string, RegistrationError>> Registration(NewUserCredentialsInput input)
    {
        var clientName = this.GetClientName();
        var ipAddress  = this.GetIpAddress();
        var region     = this.GetRegion();
        var hostName   = this.GetHostName();

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        return await grainFactory
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .Register(input, connInfo);
    }

    public async Task<Either<Server, AcceptInviteError>> JoinToServerAsync(InviteCode inviteCode)
    {
        var userData = this.GetUser();
        var invite   = grainFactory.GetGrain<IInviteGrain>(inviteCode.inviteCode);
        var result   = await invite.AcceptAsync(userData.id);

        if (result.Item2 != AcceptInviteError.NONE)
            return result.Item2;

        return await grainFactory.GetGrain<IServerGrain>(result.Item1).GetServer();
    }
}