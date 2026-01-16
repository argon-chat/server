namespace Argon.Services.Ion;

using Features.Logic;
using ArgonContracts;
using Argon.Core.Features.Logic;
using Core.Entities.Data;
using ion.runtime;

public class UserInteractionImpl(
    IOptions<BetaLimitationOptions> betaOptions,
    ILogger<IUserInteraction> logger) : IUserInteraction

{
    private IUserGrain UserGrain => this.GetGrain<IUserGrain>(this.GetUserId());
    private IUserStatsGrain UserStatsGrain => this.GetGrain<IUserStatsGrain>(this.GetUserId());
    private IUserLevelGrain UserLevelGrain => this.GetGrain<IUserLevelGrain>(this.GetUserId());

    public async Task<ArgonUser> GetMe(CancellationToken ct = default)
    {
        var user = await UserGrain.GetMe();
        return user.ToDto();
    }

    public async Task<ICreateSpaceResult> CreateSpace(CreateServerRequest request, CancellationToken ct = default)
    {
#if !DEBUG
        var callerId = this.GetUserId();
        if (!betaOptions.Value.AllowedCreationSpaceUsers.Contains(callerId))
            return new FailedCreateSpace(CreateSpaceError.LIMIT_REACHED);
#endif

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
        => new(await UserGrain.GetMyServers());

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
        => await UserGrain.BroadcastPresenceAsync(presence);

    public async Task RemoveBroadcastPresence(CancellationToken ct = default)
        => await UserGrain.RemoveBroadcastPresenceAsync();

    public async Task<IonArray<FeatureFlag>> GetMyFeatures(CancellationToken ct = default)
        => IonArray<FeatureFlag>.Empty;

    public async Task<ArgonUserProfile> GetMyProfile(CancellationToken ct = default)
        => await UserGrain.GetMyProfile();

    public async Task<IUploadFileResult> BeginUploadAvatar(CancellationToken ct = default)
    {
        var result = await UserGrain.BeginUploadUserFile(UserFileKind.Avatar, ct);
        return MapUploadResult(result);
    }

    public async Task CompleteUploadAvatar(Guid blobId, CancellationToken ct = default)
        => await UserGrain.CompleteUploadUserFile(blobId, UserFileKind.Avatar, ct);

    public async Task<IUploadFileResult> BeginUploadProfileHeader(CancellationToken ct = default)
    {
        var result = await UserGrain.BeginUploadUserFile(UserFileKind.ProfileHeader, ct);
        return MapUploadResult(result);
    }

    public async Task CompleteUploadProfileHeader(Guid blobId, CancellationToken ct = default)
        => await UserGrain.CompleteUploadUserFile(blobId, UserFileKind.ProfileHeader, ct);

    private static IUploadFileResult MapUploadResult(Either<BlobId, UploadFileError> result)
    {
        if (result.IsSuccess)
            return new SuccessUploadFile(result.Value.Id);
        return new FailedUploadFile(result.Error);
    }

    public async Task<TodayStats> GetTodayStats(CancellationToken ct = default)
    {
        return await UserStatsGrain.GetTodayStatsAsync();
    }

    public async Task<MyLevelDetails> GetMyLevel(CancellationToken ct = default)
    {
        return await UserLevelGrain.GetLevelDetailsAsync();
    }

    public async Task<bool> ClaimLevelCoin(CancellationToken ct = default)
    {
        return await UserLevelGrain.ClaimMedalAsync();
    }

    public async Task<IonArray<NotificationCounterKv>> GetNotificationCounters(CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        var counters = await this.GetGrain<INotificationCounterGrain>(userId).GetAllCountersAsync();

        var result = new[]
        {
            new NotificationCounterKv(NotificationCounterType.UnreadInventoryItems, counters.UnreadInventoryItems),
            new NotificationCounterKv(NotificationCounterType.PendingFriendRequests, counters.PendingFriendRequests),
            new NotificationCounterKv(NotificationCounterType.UnreadDirectMessages, counters.UnreadDirectMessages)
        };

        return new IonArray<NotificationCounterKv>(result);
    }
}