namespace Argon.Services.Ion;

using Features.Logic;
using ArgonContracts;
using Argon.Core.Features.Logic;
using Argon.Core.Entities.Data;
using Argon.Core.Grains.Interfaces;
using ion.runtime;

public class UserInteractionImpl(
    IOptions<BetaLimitationOptions> betaOptions,
    ILogger<IUserInteraction> logger) : IUserInteraction

{
    public async Task<ArgonUser> GetMe(CancellationToken ct = default)
    {
        var user = await this.GetGrain<IUserGrain>(this.GetUserId()).GetMe();
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
            var result = await this.GetGrain<ISpaceGrain>(Guid.CreateVersion7())
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

    public async Task<IUpdateMeResult> UpdateMe(UserEditInput request, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserGrain>(this.GetUserId()).UpdateProfileAsync(request, ct);

        if (result.IsSuccess)
            return new SuccessUpdateMe(result.Value.User, result.Value.Profile);
        return new FailedUpdateMe(result.Error);
    }

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

    public async Task<IUploadFileResult> BeginUploadAvatar(CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserGrain>(this.GetUserId()).BeginUploadUserFile(UserFileKind.Avatar, ct);

        if (result.IsSuccess)
            return new SuccessUploadFile(result.Value.Id);
        return new FailedUploadFile(result.Error);
    }

    public async Task CompleteUploadAvatar(Guid blobId, CancellationToken ct = default)
        => await this.GetGrain<IUserGrain>(this.GetUserId()).CompleteUploadUserFile(blobId, UserFileKind.Avatar, ct);

    public async Task<TodayStats> GetTodayStats(CancellationToken ct = default)
    {
        var statsGrain = this.GetGrain<IUserStatsGrain>(this.GetUserId());
        return await statsGrain.GetTodayStatsAsync();
    }

    public async Task<MyLevelDetails> GetMyLevel(CancellationToken ct = default)
    {
        var levelGrain = this.GetGrain<IUserLevelGrain>(this.GetUserId());
        return await levelGrain.GetLevelDetailsAsync();
    }

    public async Task<bool> ClaimLevelCoin(CancellationToken ct = default)
    {
        var levelGrain = this.GetGrain<IUserLevelGrain>(this.GetUserId());
        return await levelGrain.ClaimMedalAsync();
    }

    public async Task<GlobalBadges> GetGlobalBadges(CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        return await this.GetGrain<INotificationGrain>(userId).GetGlobalBadgesAsync(ct);
    }

    public async Task AckChannel(Guid channelId, long lastReadMessageId, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await this.GetGrain<INotificationGrain>(userId).AckChannelAsync(channelId, null, lastReadMessageId, ct);
    }

    public async Task MuteTarget(Guid targetId, MuteTargetKind targetType, MuteLevelType muteLevel, bool suppressEveryone, DateTime? expiresAt, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await this.GetGrain<INotificationGrain>(userId).MuteAsync(targetId, targetType, muteLevel, suppressEveryone, expiresAt, ct);
    }

    public async Task UnmuteTarget(Guid targetId, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await this.GetGrain<INotificationGrain>(userId).UnmuteAsync(targetId, ct);
    }

    public async Task<IonArray<SystemNotificationDto>> GetNotificationFeed(int limit, DateTime? before, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        var feed = await this.GetGrain<INotificationGrain>(userId).GetNotificationFeedAsync(limit, before, ct);
        return new IonArray<SystemNotificationDto>(feed.ToArray());
    }

    public async Task MarkNotificationRead(Guid notificationId, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await this.GetGrain<INotificationGrain>(userId).MarkNotificationReadAsync(notificationId, ct);
    }

    public async Task MarkAllNotificationsRead(string? type, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await this.GetGrain<INotificationGrain>(userId).MarkAllNotificationsReadAsync(type, ct);
    }
}