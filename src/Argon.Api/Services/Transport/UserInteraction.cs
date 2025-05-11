namespace Argon.Services;

using Features.Social;
using Validators;
using Shared.Servers;

public class UserInteraction(TelegramSocialBounder bounder) : IUserInteraction
{
    public async Task<User> GetMe()
    {
        var userData = this.GetUser();
        return await this.GetGrainFactory().GetGrain<IUserGrain>(userData.id).GetMe();
    }

    public async Task<ServerDto> CreateServer(CreateServerRequest request)
    {
        var userData = this.GetUser();
        var serverId = Guid.NewGuid();
        var server = await this.GetGrainFactory()
           .GetGrain<IServerGrain>(serverId)
           .CreateServer(new ServerInput(request.Name, request.Description, request.AvatarFileId), userData.id);
        return server.Value.ToDto();
    }

    public async Task<List<ServerDto>> GetServers()
    {
        var userData = this.GetUser();
        var servers  = await this.GetGrainFactory().GetGrain<IUserGrain>(userData.id).GetMyServers();
        return servers.ToDto();
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
    public async Task<Either<string, RegistrationErrorData>> Registration(NewUserCredentialsInput input)
    {
        var validationResult = await new NewUserCredentialsInputValidator().ValidateAsync(input);

        if (!validationResult.IsValid)
        {
            var err = validationResult.Errors.First();
            return new RegistrationErrorData()
            {
                Field   = err.PropertyName,
                Code    = RegistrationError.VALIDATION_FAILED,
                Message = err.ErrorMessage
            };
        }

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

    [AllowAnonymous]
    public async Task<bool> CompleteSocialBoundAsync(string token, string socialUser, string kind, string userSlash)
    {
        if (!Guid.TryParse(userSlash.Split('/').First(), out var userId))
            return false;
        if (!kind.Equals("Telegram")) // temporary only tg
            return false;
        var clusterClient = this.GetClusterClient();

        return await bounder.CompleteBoundTokenAsync(userId, token, socialUser, clusterClient);
    }

    public async Task<bool> DeleteSocialBound(string kind, Guid socialId)
        => await this.GetGrainFactory().GetGrain<IUserGrain>(this.GetUser().id).DeleteSocialBoundAsync(kind, socialId);


    public async Task<string> CreateSocialBoundAsync(string kind)
    {
        if (!kind.Equals("Telegram")) // temporary only tg
            return "";
        var userData = this.GetUser();

        var token = await bounder.CreateBoundTokenAsync(userData.id, TimeSpan.FromMinutes(2));

        return token;
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

    public async Task BroadcastPresenceAsync(UserActivityPresence presence)
    {
        var userData = this.GetUser();
        await this.GetGrainFactory().GetGrain<IUserGrain>(userData.id).BroadcastPresenceAsync(presence);
    }

    public async Task RemoveBroadcastPresenceAsync()
    {
        var userData = this.GetUser();
        await this.GetGrainFactory().GetGrain<IUserGrain>(userData.id).RemoveBroadcastPresenceAsync();
    }


    public async Task<List<UserSocialIntegrationDto>> GetMeSocials()
    {
        var userData = this.GetUser();
        return await this.GetGrainFactory().GetGrain<IUserGrain>(userData.id).GetMeSocials();
    }
}