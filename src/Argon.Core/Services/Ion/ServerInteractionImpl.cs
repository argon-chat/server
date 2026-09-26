namespace Argon.Services.Ion;

using ion.runtime;
using Microsoft.Extensions.Configuration;
using InviteCode = ArgonContracts.InviteCode;

public class ServerInteractionImpl(IConfiguration configuration) : IServerInteraction
{
    private const string DefaultInviteDomain = "https://argon.gl/i";

    [Obsolete("Superseded by GetSpaceSnapshot; remove once no shipped client calls it.")]
    public async Task<IonArray<ChannelGroup>> GetChannelGroups(Guid spaceId, CancellationToken ct = default)
    {
        var groups = await this
           .GetGrain<ISpaceReadGrain>(spaceId)
           .GetChannelGroups();

        return new IonArray<ChannelGroup>(groups);
    }

    public async Task<SpaceSnapshot> GetSpaceSnapshot(Guid spaceId, SpaceVersions? known, CancellationToken ct = default)
        => await this.GetGrain<ISpaceReadGrain>(spaceId).GetSnapshot(known);

    public async Task<IonArray<MemberPresence>> GetMemberPresence(Guid spaceId, CancellationToken ct = default)
        => new(await this.GetGrain<ISpaceReadGrain>(spaceId).GetPresence());

    [Obsolete("Superseded by GetSpaceSnapshot; remove once no shipped client calls it.")]
    public async Task<IonArray<RealtimeServerMember>> GetMembers(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceReadGrain>(spaceId)
           .GetMembers();
        return new IonArray<RealtimeServerMember>(result);
    }

    public async Task<RealtimeServerMember> GetMember(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .GetMember(userId);

    public async Task<IGetInviteCodesResult> GetInviteCodes(Guid spaceId, CancellationToken ct = default)
    {
        var (error, result) = await this.GetGrain<IServerInvitesGrain>(spaceId)
           .GetInviteCodes(this.GetUserId());

        if (error is not SpaceManageError.NONE)
            return new FailedGetInviteCodes(error);

        var invites = new IonArray<InviteCodeEntity>(result.Select(x
            => new InviteCodeEntity(new InviteCode(x.code.inviteCode), x.spaceId, x.issuerId, x.expireTime.UtcDateTime,
                (ulong)x.used, x.maxUses, x.createdAt.UtcDateTime)));
        var domain = configuration["Invites:Domain"] ?? DefaultInviteDomain;
        return new SuccessGetInviteCodes(new ServerInvites(domain, invites));
    }

    public async Task<ICreateInviteCodeResult> CreateInviteCode(Guid spaceId, int expireMinutes, int maxUses, CancellationToken ct = default)
    {
        // expireMinutes <= 0 means "never" — model it as a far-future timestamp so the TTL sweeper leaves it be.
        var expiration = expireMinutes <= 0
            ? TimeSpan.FromDays(365 * 100)
            : TimeSpan.FromMinutes(expireMinutes);

        var (error, result) = await this
           .GetGrain<IServerInvitesGrain>(spaceId)
           .CreateInviteLinkAsync(this.GetUserId(), expiration, maxUses);

        return error is SpaceManageError.NONE
            ? new SuccessCreateInviteCode(new InviteCode(result.inviteCode))
            : new FailedCreateInviteCode(error);
    }

    public async Task<ISpaceManageResult> RevokeInviteCode(Guid spaceId, InviteCode code, CancellationToken ct = default)
        => Manage(await this.GetGrain<IServerInvitesGrain>(spaceId).RevokeInviteAsync(this.GetUserId(), code.inviteCode));

    public async Task<ISpaceManageResult> UpdateSpaceInfo(Guid spaceId, string name, string description, CancellationToken ct = default)
        => Manage(await this.GetGrain<ISpaceGrain>(spaceId).UpdateSpace(new ServerInput(name, description, null)));

    public async Task<ISpaceManageResult> SetBoostStripHidden(Guid spaceId, bool hidden, CancellationToken ct = default)
        => Manage(await this.GetGrain<ISpaceGrain>(spaceId).SetBoostStripHidden(hidden));

    private static ISpaceManageResult Manage(SpaceManageError error)
        => error is SpaceManageError.NONE
            ? new SuccessSpaceManage()
            : new FailedSpaceManage(error);

    public async Task<SpaceStats> GetSpaceStats(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).GetSpaceStats();

    public async Task<IRequestDeleteSpaceResult> RequestDeleteSpace(Guid spaceId, CancellationToken ct = default)
    {
        var (error, deletionState) = await this.GetGrain<ISpaceDeletionGrain>(spaceId).RequestAsync(this.GetUserId());

        return error is SpaceDeletionError.NONE
            ? new SuccessRequestDeleteSpace(deletionState)
            : new FailedRequestDeleteSpace(error);
    }

    public async Task<ICancelDeleteSpaceResult> CancelDeleteSpace(Guid spaceId, CancellationToken ct = default)
    {
        var error = await this.GetGrain<ISpaceDeletionGrain>(spaceId).CancelAsync(this.GetUserId());

        return error is SpaceDeletionError.NONE
            ? new SuccessCancelDeleteSpace()
            : new FailedCancelDeleteSpace(error);
    }

    public async Task<SpaceDeletionState> GetSpaceDeletionState(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceDeletionGrain>(spaceId).GetStateAsync();

    public async Task<MemberEntitlements> GetMyEntitlements(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceReadGrain>(spaceId).GetMemberEntitlements();

    public async Task<IVoiceModerationResult> SetMemberVoiceModeration(Guid spaceId, Guid memberId, bool? muted, bool? deafened,
        CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).SetMemberVoiceModeration(memberId, muted, deafened, ct);

    public async Task<ArgonUser> PrefetchUser(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).PrefetchUser(userId, ct);

    public async Task<ArgonUserProfile> PrefetchProfile(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).PrefetchProfile(userId);

    public async Task<IonArray<ArgonUserProfile>> PrefetchProfiles(Guid spaceId, IonArray<Guid> userIds, CancellationToken ct = default)
        => new(await this.GetGrain<ISpaceGrain>(spaceId).PrefetchProfiles(userIds.Values.ToList()));

    [Obsolete("Superseded by GetSpaceSnapshot; remove once no shipped client calls it.")]
    public async Task<IonArray<RealtimeChannel>> GetChannels(Guid spaceId, CancellationToken ct = default)
        => new(await this.GetGrain<ISpaceReadGrain>(spaceId)
           .GetChannels());

    public async Task<IonArray<Archetype>> GetServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

    public async Task<IonArray<ArchetypeGroup>> GetDetailedServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetFullyServerArchetypes();

    public async Task<IUploadFileResult> BeginUploadSpaceProfileHeader(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.ProfileHeader, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
        return new FailedUploadFile(result.Error);
    }

    public async Task<ISpaceManageResult> CompleteUploadSpaceProfileHeader(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => Manage(await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.ProfileHeader, ct));

    public async Task<IUploadFileResult> BeginUploadSpaceAvatar(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.Avatar, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
        return new FailedUploadFile(result.Error);
    }

    public async Task<ISpaceManageResult> CompleteUploadSpaceAvatar(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => Manage(await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.Avatar, ct));

    public async Task<IUploadFileResult> BeginUploadInviteImage(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.InviteImage, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
        return new FailedUploadFile(result.Error);
    }

    public async Task<ISpaceManageResult> CompleteUploadInviteImage(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => Manage(await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.InviteImage, ct));
}
