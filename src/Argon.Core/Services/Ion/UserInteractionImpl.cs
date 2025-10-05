namespace Argon.Services.Ion;

using Features.Logic;
using ArgonContracts;
using ion.runtime;

public class UserInteractionImpl(IOptions<BetaLimitationOptions> betaOptions, ILogger<IUserInteraction> logger) : IUserInteraction
{
    public async Task<ArgonUser> GetMe(CancellationToken ct = default)
    {
        var user = await this.GetGrain<IUserGrain>(this.GetUserId()).GetMe();
        return user.ToDto();
    }

    public async Task<ICreateSpaceResult> CreateSpace(CreateServerRequest request, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();

        if (!betaOptions.Value.AllowedCreationSpaceUsers.Contains(callerId))
            return new FailedCreateSpace(CreateSpaceError.LIMIT_REACHED);

        try
        {
            var result = await this.GetGrain<ISpaceGrain>(this.GetUserId())
               .CreateSpace(new ServerInput(request.name, request.description, request.avatarFieldId));
            return new SuccessCreateSpace(result.Value);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed create space");
            return new FailedCreateSpace(CreateSpaceError.UNKNOWN);
        }
    }

    public async Task<IonArray<ArgonSpaceBase>> GetSpaces(CancellationToken ct = default)
        => new(await this.GetGrain<IUserGrain>(this.GetUserId()).GetMyServers());

    public Task<ArgonUser> UpdateMe(UserEditInput request, CancellationToken ct = default)
        => throw new NotImplementedException();

    public async Task<IJoinToSpaceResult> JoinToSpace(InviteCode inviteCode, CancellationToken ct = default)
    {
        var invite = this.GetGrain<IInviteGrain>(inviteCode.inviteCode);
        var result = await invite.AcceptAsync();

        if (result.Item2 != AcceptInviteError.NONE)
            return new FailedJoin(result.Item2);
        var space = await this.GetGrain<ISpaceGrain>(result.Item1).GetSpace();
        return new SuccessJoin(space.ToDto());
    }

    public async Task BroadcastPresence(UserActivityPresence presence, CancellationToken ct = default)
        => await this.GetGrain<IUserGrain>(this.GetUserId()).BroadcastPresenceAsync(presence);

    public async Task RemoveBroadcastPresence(CancellationToken ct = default)
        => await this.GetGrain<IUserGrain>(this.GetUserId()).RemoveBroadcastPresenceAsync();

    public async Task<IonArray<FeatureFlag>> GetMyFeatures(CancellationToken ct = default)
        => IonArray<FeatureFlag>.Empty;

    public async Task<ArgonUserProfile> GetMyProfile(CancellationToken ct = default)
        => await this.GetGrain<IUserGrain>(this.GetUserId()).GetMyProfile();
}