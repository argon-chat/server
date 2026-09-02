namespace Argon.Services.Ion;
using Argon.Features.Clustering.Regions;

using Features.Logic;
using ArgonContracts;
using Argon.Core.Features.Logic;
using Argon.Core.Entities.Data;
using Argon.Core.Grains.Interfaces;
using ion.runtime;

public class UserInteractionImpl(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IUserInteraction> logger) : IUserInteraction

{
    private const int MaxOwnedSpacesPerUser = 10;

    public async Task<ArgonUser> GetMe(CancellationToken ct = default)
    {
        var user = await this.GetGrain<IUserGrain>(this.GetUserId()).GetMe();
        return user.ToDto();
    }

    public Task<LegalState> GetMyLegalState(CancellationToken ct = default)
        => this.GetGrain<IUserGrain>(this.GetUserId()).GetLegalState();

    public Task<LegalState> AcceptLegal(AcceptLegalInput request, CancellationToken ct = default)
        => this.GetGrain<IUserGrain>(this.GetUserId()).AcceptLegal(request.tosVersion, request.privacyVersion);

    public async Task<ICreateSpaceResult> CreateSpace(CreateServerRequest request, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();

        await using (var ctx = await context.CreateDbContextAsync(ct))
        {
            var ownedCount = await ctx.Spaces.CountAsync(s => s.CreatorId == callerId, ct);
            if (ownedCount >= MaxOwnedSpacesPerUser)
                return new FailedCreateSpace(CreateSpaceError.LIMIT_REACHED);
        }

        try
        {
            // The space's home region for the rest of its life, and the reason a call about it can be
            // routed without asking anything: it is in the key.
            var result = await this.GetGrain<ISpaceGrain>(ArgonId.New())
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
        this.EnforceLockdown(LockdownSeverity.Middle);

        var invite = this.GetGrain<IInviteGrain>(inviteCode.inviteCode);
        var result = await invite.AcceptAsync();

        if (result.Item2 != AcceptInviteError.NONE)
            return new FailedJoin(result.Item2);
        var space = await this.GetGrain<ISpaceGrain>(result.Item1).GetSpace();
        return new SuccessJoin(space.ToDto());
    }

    public async Task<IPreviewInviteResult> PreviewInvite(InviteCode inviteCode, CancellationToken ct = default)
    {
        var (target, error) = await this.GetGrain<IInviteGrain>(inviteCode.inviteCode).PreviewAsync();

        if (error != AcceptInviteError.NONE || target is null)
            return new FailedPreview(error == AcceptInviteError.NONE ? AcceptInviteError.NOT_FOUND : error);

        var preview = await this.GetGrain<ISpaceGrain>(target.SpaceId).GetInvitePreview();

        // The space grain builds the generic preview; only the invite knows it was minted for a room,
        // so the room is stitched on here rather than pushed down into GetInvitePreview.
        return new SuccessPreview(preview with
        {
            voiceChannelId = target.VoiceChannelId,
            voiceChannelName = target.VoiceChannelName
        });
    }

    public async Task BroadcastPresence(UserActivityPresence presence, CancellationToken ct = default)
        => await this.GetGrain<IUserGrain>(this.GetUserId()).BroadcastPresenceAsync(presence, this.GetSessionId().ToString());

    public async Task RemoveBroadcastPresence(CancellationToken ct = default)
        // Explicit user action — always broadcast the clear, even if the activity key already lapsed.
        => await this.GetGrain<IUserGrain>(this.GetUserId()).RemoveBroadcastPresenceAsync(this.GetSessionId().ToString(), alwaysBroadcast: true);

    /// <summary>
    /// The caller's evaluated flag set.
    /// </summary>
    /// <remarks>
    /// <para>Delegates to the same grain as <c>FeatureFlagInteractions.GetMyFeatureFlags</c> rather
    /// than evaluating separately: two surfaces answering the same question is already one too
    /// many, and two surfaces answering it <em>differently</em> would be a bug nobody could see
    /// from either side. This returned an empty array before, which no caller could tell apart from
    /// "you have no flags".</para>
    ///
    /// <para><c>parameters</c> is empty because evaluation does not produce any — the flag carries a
    /// variant, not a parameter bag. Reporting an empty list is the truth; inventing entries to
    /// fill the field would not be.</para>
    /// </remarks>
    public async Task<IonArray<FeatureFlag>> GetMyFeatures(CancellationToken ct = default)
    {
        var evaluated = await this.GetGrain<IFeatureFlagGrain>(Guid.Empty)
           .EvaluateAllAsync(FeatureFlagEvaluationContext.ForUser(
                this.GetUserId(), this.GetUserCountry(), this.GetClientId()));

        return new IonArray<FeatureFlag>(evaluated.Values
           .Select(x => new FeatureFlag(x.FlagId, x.IsEnabled, x.Variant, IonArray<FeatureFlagParameter>.Empty))
           .ToArray());
    }

    public async Task<ArgonUserProfile> GetMyProfile(CancellationToken ct = default)
        => await this.GetGrain<IUserGrain>(this.GetUserId()).GetMyProfile();

    public async Task<ILookupUserResult> LookupUser(Guid userId, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();

        if (!await CanReachAsync(callerId, userId, ct))
            return new FailedLookupUser(LookupError.NO_ANCHOR);

        var user = await this.GetGrain<IUserGrain>(userId).GetMe();

        return user is null
            ? new FailedLookupUser(LookupError.NOT_FOUND)
            : new SuccessLookupUser(user.ToDto());
    }

    public async Task<ILookupProfileResult> LookupProfile(Guid userId, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();

        if (!await CanReachAsync(callerId, userId, ct))
            return new FailedLookupProfile(LookupError.NO_ANCHOR);

        try
        {
            return new SuccessLookupProfile(await this.GetGrain<IUserGrain>(userId).GetMyProfile());
        }
        catch (InvalidOperationException)
        {
            // No profile row: the id is well-formed but nobody is behind it.
            return new FailedLookupProfile(LookupError.NOT_FOUND);
        }
    }

    /// <summary>
    /// Whether the caller has any standing reason to know this person.
    /// </summary>
    /// <remarks>
    /// <para>The space id on <c>PrefetchUser</c> was doing two jobs: naming which space's nickname
    /// and roles to show, and proving the caller had met the target at all. Dropping it for direct
    /// messages drops the first, and this restores the second — otherwise a bare user id would be
    /// enough to walk the whole directory, which is exactly what scoping prevented.</para>
    ///
    /// <para>Ordered cheapest-first, and each anchor is a relationship the target took part in:
    /// sharing a space, being friends, a request in either direction, or a conversation that
    /// exists. A pending request counts because the target has to be able to see who is asking.</para>
    /// </remarks>
    private async Task<bool> CanReachAsync(Guid callerId, Guid targetId, CancellationToken ct)
    {
        // One definition, shared with the report system: what is enough standing to look someone
        // up is exactly enough standing to report them. See SocialReach.
        await using var ctx = await context.CreateDbContextAsync(ct);

        return await Argon.Api.Features.CoreLogic.Social.SocialReach.CanReachAsync(ctx, callerId, targetId, ct);
    }

    public async Task<IUploadFileResult> BeginUploadAvatar(CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserGrain>(this.GetUserId()).BeginUploadUserFile(UserFileKind.Avatar, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
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

    public async Task MuteTarget(Guid targetId, MuteTargetKind targetType, MuteLevelType muteLevel, bool suppressEveryone, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await this.GetGrain<INotificationGrain>(userId).MuteAsync(targetId, targetType, muteLevel, suppressEveryone, expiresAt?.UtcDateTime, ct);
    }

    public async Task UnmuteTarget(Guid targetId, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await this.GetGrain<INotificationGrain>(userId).UnmuteAsync(targetId, ct);
    }

    public async Task<IonArray<SystemNotificationDto>> GetNotificationFeed(int limit, DateTimeOffset? before, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        var feed = await this.GetGrain<INotificationGrain>(userId).GetNotificationFeedAsync(limit, before?.UtcDateTime, ct);
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
