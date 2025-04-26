namespace Argon.Services;

using Shared.Servers;

public class UserInteraction : IUserInteraction
{
    public async Task<User> GetMe()
    {
        var userData = this.GetUser();
        return await this.GetGrainFactory().GetGrain<IUserGrain>(userData.id).GetMe();
    }
    public async Task<Server> CreateServer(CreateServerRequest request)
    {
        var userData = this.GetUser();
        var serverId = Guid.NewGuid();
        var server   = await this.GetGrainFactory()
           .GetGrain<IServerGrain>(serverId)
           .CreateServer(new ServerInput(request.Name, request.Description, request.AvatarFileId), userData.id);
        return server.Value;
    }

    public async Task<List<Server>> GetServers()
    {
        var userData = this.GetUser();
        var servers  = await this.GetGrainFactory().GetGrain<IUserGrain>(userData.id).GetMyServers();
        return servers;
    }

    [AllowAnonymous]
    public async Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input)
    {
        var clientName = this.GetClientName();
        var ipAddress  = this.GetIpAddress();
        var region     = this.GetRegion();
        var hostName   = this.GetHostName();
        var machineId  = this.TryGetMachineId();


        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName, machineId);

        var result = await this.GetGrainFactory()
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
        var machineId  = this.TryGetMachineId();

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName, machineId);

        return await this.GetGrainFactory()
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .Register(input, connInfo);
    }

    [AllowAnonymous]
    public Task<bool> BeginResetPassword(string email)
    {
        var clientName = this.GetClientName();
        var ipAddress  = this.GetIpAddress();
        var region     = this.GetRegion();
        var hostName   = this.GetHostName();
        var machineId  = this.TryGetMachineId();

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName, machineId);

        return this.GetGrainFactory()
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .BeginResetPass(email, connInfo);
    }

    [AllowAnonymous]
    public async Task<Either<string, AuthorizationError>> ResetPassword(UserResetPassInput input)
    {
        var clientName = this.GetClientName();
        var ipAddress  = this.GetIpAddress();
        var region     = this.GetRegion();
        var hostName   = this.GetHostName();
        var machineId  = this.TryGetMachineId();

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName, machineId);

        return await this.GetGrainFactory()
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .ResetPass(input, connInfo);
    }

    public async Task<Either<Server, AcceptInviteError>> JoinToServerAsync(InviteCode inviteCode)
    {
        var userData = this.GetUser();
        var invite   = this.GetGrainFactory().GetGrain<IInviteGrain>(inviteCode.inviteCode);
        var result   = await invite.AcceptAsync(userData.id);

        if (result.Item2 != AcceptInviteError.NONE)
            return result.Item2;

        return await this.GetGrainFactory().GetGrain<IServerGrain>(result.Item1).GetServer();
    }   
}