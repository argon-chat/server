namespace Argon.Services.Ion;

using ArgonContracts;
using ion.runtime;

public class UserInteractionImpl : IUserInteraction
{
    public async Task<ArgonUser> GetMe()
    {
        var user = await this.GetGrain<IUserGrain>(this.GetUserId()).GetMe();
        return user.ToDto();
    }

    public async Task<ArgonSpaceBase> CreateSpace(CreateServerRequest request)
    {
        var result = await this.GetGrain<ISpaceGrain>(this.GetUserId())
           .CreateSpace(new ServerInput(request.name, request.description, request.avatarFieldId));
        return result.Value;
    }

    public async Task<IonArray<ArgonSpaceBase>> GetSpaces()
        => new(await this.GetGrain<IUserGrain>(this.GetUserId()).GetMyServers());

    public Task<ArgonUser> UpdateMe(UserEditInput request)
        => throw new NotImplementedException();

    public async Task<IAuthorizeResult> Authorize(UserCredentialsInput data)
    {
        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).Authorize(data);

        if (result.IsSuccess)
            return new SuccessAuthorize(result.Value, null);
        return new FailedAuthorize(result.Error);
    }

    public async Task<IRegistrationResult> Registration(NewUserCredentialsInput data)
    {
        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).Register(data);

        if (result.IsSuccess)
            return new SuccessRegistration(result.Value, null);
        return new FailedRegistration(result.Error.error, result.Error.field, result.Error.message);
    }

    public Task<bool> BeginResetPassword(string email)
        => throw new NotImplementedException();

    public Task<IAuthorizeResult> ResetPassword(string email, string otpCode, string newPassword)
        => throw new NotImplementedException();

    public async Task<IJoinToSpaceResult> JoinToSpace(InviteCode inviteCode)
    {
        var invite = this.GetGrain<IInviteGrain>(inviteCode.inviteCode);
        var result = await invite.AcceptAsync();

        if (result.Item2 != AcceptInviteError.NONE)
            return new FailedJoin(result.Item2);
        var space = await this.GetGrain<ISpaceGrain>(result.Item1).GetSpace();
        return new SuccessJoin(space.ToDto());
    }

    public async Task BroadcastPresence(UserActivityPresence presence)
        => await this.GetGrain<IUserGrain>(this.GetUserId()).BroadcastPresenceAsync(presence);

    public async Task RemoveBroadcastPresence()
        => await this.GetGrain<IUserGrain>(this.GetUserId()).RemoveBroadcastPresenceAsync();

    public async Task<IonArray<FeatureFlag>> GetMyFeatures()
        => IonArray<FeatureFlag>.Empty;
}