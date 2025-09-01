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

    public async Task<ArgonUserProfile> GetMyProfile()
        => await this.GetGrain<IUserGrain>(this.GetUserId()).GetMyProfile();

    public async Task<IonArray<InventoryItem>> GetMyInventoryItems()
    {
        // for testing purpose
        var grantedUsers = new List<Guid>()
        {
            Guid.Parse("55958b47-414b-4fb4-b42c-1a660e2bda3a"),
            Guid.Parse("d3d02844-7592-40da-ab92-53e06a596cbc"),
            Guid.Parse("cf782028-abaf-4958-af3f-db68636e28e4"),
            Guid.Parse("bd76098b-dc53-4d42-a8f6-9ae41b3e5d21"),
            Guid.Parse("b7404c69-abf2-4d73-b7b0-f4f232c85815"),
            Guid.Parse("160c65a4-1baa-4336-8377-0ee7736ce193"),
            Guid.Parse("dfbcd9a2-70e1-4f0f-85fc-8e383625e982"),
            Guid.Parse("7becb726-e5d0-42a8-9fa6-0b2826423de8"),
            Guid.Parse("bdc95de9-2868-4ff9-a5fd-461011f288d4"),
        };
        if (grantedUsers.Any(x => x == this.GetUserId()))
            return new IonArray<InventoryItem>([new InventoryItem("magic_coal", DateTime.Parse("01.09.2025 0:00:00"))]);
        return IonArray<InventoryItem>.Empty;
    }
}