namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using static SpaceGroupSupport;

/// <summary>
/// What a space says about itself — its name, its pictures, its badges, its boost strip — and who
/// may change it.
/// </summary>
/// <remarks>
/// <para>Every write here is gated on <c>ManageServer</c> except the platform flags, which only the
/// operator console reaches and which therefore check no caller at all. So each area is pinned from
/// both sides: the owner's change is what the space reports afterwards, and a plain member's attempt
/// changes nothing.</para>
///
/// <para>The pictures go through a real object store, because the grain never sees the bytes — it
/// signs a URL, the client uploads, and the grain is asked to confirm afterwards.</para>
/// </remarks>
[TestFixture]
public class SpaceSettingsTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private static async Task<ArgonSpaceBase> SpaceAsync(TestUserSession member, Guid spaceId, CancellationToken ct)
        => (await member.Users.GetSpaces(ct)).Values.Single(s => s.spaceId == spaceId);

    private static async Task<(TestUserSession Owner, TestUserSession Member, Guid SpaceId)> OwnerAndMemberAsync(
        Func<Task<TestUserSession>> session, string name, CancellationToken ct)
    {
        var owner   = await session();
        var member  = await session();
        var spaceId = await CreateSpaceAsync(owner, name, ct);

        await JoinAsync(owner, member, spaceId, ct);
        return (owner, member, spaceId);
    }

    // ── Name and description ────────────────────────────────────────────────────────────────────

    [TestCase("")]
    [TestCase("   ")]
    [CancelAfter(120_000)]
    public async Task CreateSpace_WithoutAUsableName_IsRefused(string name, CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var blank   = await owner.Users.CreateSpace(new CreateServerRequest(name, "Description", string.Empty), ct);
        var tooLong = await owner.Users.CreateSpace(new CreateServerRequest(new string('s', 65), "Description", string.Empty), ct);
        var spaces  = await owner.Users.GetSpaces(ct);

        Assert.Multiple(() =>
        {
            Assert.That(blank, Is.InstanceOf<FailedCreateSpace>());
            Assert.That(tooLong, Is.InstanceOf<FailedCreateSpace>());
            Assert.That(spaces.Values, Is.Empty, "a refused space was created anyway");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task UpdateSpaceInfo_ChangesWhatTheSpaceSaysAndTellsItsMembers(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await OwnerAndMemberAsync(() => CreateSessionAsync(ct), "Before", ct);

        await using var watcher = await RealtimeClient.ConnectAsync(member, ct);
        await watcher.SubscribeToSpace(spaceId, ct);
        var mark = watcher.Mark();

        await owner.Servers.UpdateSpaceInfo(spaceId, "After", "A new description", ct).Ok();

        var announced = await watcher.WaitForAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, EventWait, mark, ct);
        var seen      = await SpaceAsync(member, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(announced.details.name, Is.EqualTo("After"));
            Assert.That(seen.name, Is.EqualTo("After"));
            Assert.That(seen.description, Is.EqualTo("A new description"));
        });

        // A blank name is "leave it", which is how the settings sheet sends a description-only edit.
        await owner.Servers.UpdateSpaceInfo(spaceId, "  ", "Only the description", ct).Ok();
        seen = await SpaceAsync(member, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(seen.name, Is.EqualTo("After"));
            Assert.That(seen.description, Is.EqualTo("Only the description"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task UpdateSpaceInfo_IsRefusedWithoutManageServerOrPastTheLimits(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await OwnerAndMemberAsync(() => CreateSessionAsync(ct), "Guarded", ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await member.Servers.UpdateSpaceInfo(spaceId, "Hijacked", "x", ct),
                Is.EqualTo(new FailedSpaceManage(SpaceManageError.NO_PERMISSION)));
            Assert.That(await owner.Servers.UpdateSpaceInfo(spaceId, new string('s', 65), "x", ct),
                Is.EqualTo(new FailedSpaceManage(SpaceManageError.INVALID_DATA)));
            Assert.That(await owner.Servers.UpdateSpaceInfo(spaceId, "Fine", new string('d', 1025), ct),
                Is.EqualTo(new FailedSpaceManage(SpaceManageError.INVALID_DATA)));
        });

        var seen = await SpaceAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(seen.name, Is.EqualTo("Guarded"));
            Assert.That(seen.description, Is.EqualTo("Description"));
        });
    }

    // ── Boost strip, badges and stats ───────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SetBoostStripHidden_IsWhatTheSpaceReportsAfterwards(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await OwnerAndMemberAsync(() => CreateSessionAsync(ct), "Strip", ct);

        await owner.Servers.SetBoostStripHidden(spaceId, true, ct).Ok();
        Assert.That((await SpaceAsync(member, spaceId, ct)).hideBoostStrip, Is.True);

        Assert.That(await member.Servers.SetBoostStripHidden(spaceId, false, ct),
            Is.EqualTo(new FailedSpaceManage(SpaceManageError.NO_PERMISSION)));
        Assert.That((await SpaceAsync(member, spaceId, ct)).hideBoostStrip, Is.True, "a member without ManageServer changed it");

        await owner.Servers.SetBoostStripHidden(spaceId, false, ct).Ok();
        Assert.That((await SpaceAsync(member, spaceId, ct)).hideBoostStrip, Is.False);
    }

    /// <summary>
    /// The operator console's two badge buttons share one entry point, so a null must leave the other
    /// badge exactly as it was — and a call with neither is nothing at all.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task SetPlatformSpaceFlags_FlipsOnlyTheFlagsItIsGiven(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Badges", ct);

        await using var watcher = await RealtimeClient.ConnectAsync(owner, ct);
        await watcher.SubscribeToSpace(spaceId, ct);

        var grain = SpaceGrain(spaceId);

        var mark = watcher.Mark();
        await grain.SetPlatformSpaceFlags(isCommunity: true, isOfficial: null);
        var announced = await watcher.WaitForAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, EventWait, mark, ct);
        var afterCommunity = await SpaceAsync(owner, spaceId, ct);

        await grain.SetPlatformSpaceFlags(isCommunity: null, isOfficial: true);
        var afterOfficial = await SpaceAsync(owner, spaceId, ct);

        mark = watcher.Mark();
        await grain.SetPlatformSpaceFlags(isCommunity: null, isOfficial: null);
        var afterNothing = await SpaceAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(announced.details.isCommunity, Is.True);
            Assert.That((afterCommunity.isCommunity, afterCommunity.isOfficial), Is.EqualTo(((bool?)true, false)));
            Assert.That((afterOfficial.isCommunity, afterOfficial.isOfficial), Is.EqualTo(((bool?)true, true)));
            Assert.That((afterNothing.isCommunity, afterNothing.isOfficial), Is.EqualTo(((bool?)true, true)));
        });

        await watcher.AssertNoneWithinAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, TimeSpan.FromSeconds(1),
            "a call that changes no flag announced a change", mark, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task GetSpaceStats_CountsTheChannelsAndMembersThatAreThere(CancellationToken ct = default)
    {
        var (owner, _, spaceId) = await OwnerAndMemberAsync(() => CreateSessionAsync(ct), "Stats", ct);

        await CreateChannelAsync(owner, spaceId, "kept", ct: ct);
        var gone = await CreateChannelAsync(owner, spaceId, "gone", ct: ct);
        await CreateChannelAsync(owner, spaceId, "voice", ChannelType.Voice, ct: ct);
        await owner.Channels.DeleteChannel(spaceId, gone, ct).Ok();

        var stats = await owner.Servers.GetSpaceStats(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stats.memberCount, Is.EqualTo(2));
            Assert.That(stats.channelCount, Is.EqualTo(2), "a deleted channel is still counted");
            Assert.That(stats.boostLevel, Is.EqualTo(0));
            Assert.That(stats.createdAt, Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(5)));
        });
    }

    /// <summary>A space nobody has boosted is level zero — asked of a boost grain that has never recounted.</summary>
    [Test, CancelAfter(120_000)]
    public async Task GetSpaceBoostStatus_OfASpaceNobodyBoosted_IsLevelZero(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Unboosted", ct);

        var status = await Ultima(owner).GetSpaceBoostStatus(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(status.boostCount, Is.EqualTo(0));
            Assert.That(status.boostLevel, Is.EqualTo(0));
            Assert.That(status.boosters.Values, Is.Empty);
        });
    }

    // ── Pictures ────────────────────────────────────────────────────────────────────────────────

    private static async Task<SuccessUploadFile> BeginAsync(Func<Task<IUploadFileResult>> begin)
    {
        var result = await begin();

        Assert.That(result, Is.InstanceOf<SuccessUploadFile>(), $"the server would not sign the upload: {(result as FailedUploadFile)?.error}");
        return (SuccessUploadFile)result;
    }

    /// <summary>
    /// A new space avatar is what the space reports afterwards — including on an invite sheet that
    /// was already looked at before the change.
    /// </summary>
    /// <remarks>
    /// The invite preview is cached for thirty seconds under the space's read tag. The rename and the
    /// badge paths drop that tag; the picture path did not, so a link shared right after a new avatar
    /// went up kept showing the old one.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_space_avatar_is_attached_once_its_bytes_are_stored(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Avatar", ct);
        var invite  = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();

        var before = await owner.Users.PreviewInvite(invite, ct);
        Assert.That(((SuccessPreview)before).preview.avatarFileId, Is.Null.Or.Empty);

        var ticket = await BeginAsync(() => owner.Servers.BeginUploadSpaceAvatar(spaceId, ct));
        await UploadAsync(ticket, Png);
        await owner.Servers.CompleteUploadSpaceAvatar(spaceId, ticket.blobId, ct).Ok();

        var space   = await SpaceAsync(owner, spaceId, ct);
        var preview = ((SuccessPreview)await owner.Users.PreviewInvite(invite, ct)).preview;

        Assert.Multiple(() =>
        {
            Assert.That(space.avatarFieldId, Is.Not.Null.And.Not.Empty);
            Assert.That(preview.avatarFileId, Is.EqualTo(space.avatarFieldId), "the invite sheet still shows the old avatar");
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task A_space_header_is_attached_once_its_bytes_are_stored(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Header", ct);

        var ticket = await BeginAsync(() => owner.Servers.BeginUploadSpaceProfileHeader(spaceId, ct));
        await UploadAsync(ticket, Png);
        await owner.Servers.CompleteUploadSpaceProfileHeader(spaceId, ticket.blobId, ct).Ok();

        var space = await SpaceAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(space.topBannerFileId, Is.Not.Null.And.Not.Empty);
            Assert.That(space.avatarFieldId, Is.Null.Or.Empty, "the header went into the avatar slot");
        });
    }

    /// <summary>The full-screen invite splash is a premium surface: verified or official spaces only.</summary>
    [Test, CancelAfter(300_000)]
    public async Task An_invite_image_needs_an_official_or_verified_space(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Splash", ct);

        var refused = await owner.Servers.BeginUploadInviteImage(spaceId, ct);
        Assert.That((refused as FailedUploadFile)?.error, Is.EqualTo(UploadFileError.NOT_AUTHORIZED));

        await SpaceGrain(spaceId).SetPlatformSpaceFlags(isCommunity: null, isOfficial: true);

        var ticket = await BeginAsync(() => owner.Servers.BeginUploadInviteImage(spaceId, ct));
        await UploadAsync(ticket, Png);
        await owner.Servers.CompleteUploadInviteImage(spaceId, ticket.blobId, ct).Ok();

        Assert.That((await SpaceAsync(owner, spaceId, ct)).inviteImageFileId, Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(300_000)]
    public async Task Space_pictures_cannot_be_changed_without_ManageServer(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await OwnerAndMemberAsync(() => CreateSessionAsync(ct), "Pictures", ct);

        var avatar = await member.Servers.BeginUploadSpaceAvatar(spaceId, ct);
        var header = await member.Servers.BeginUploadSpaceProfileHeader(spaceId, ct);

        // A ticket the member signed for their own account, offered as the space's avatar.
        var own = await member.Users.BeginUploadAvatar(ct);
        Assert.That(own, Is.InstanceOf<SuccessUploadFile>());
        await UploadAsync((SuccessUploadFile)own, Png);

        var completed = await member.Servers.CompleteUploadSpaceAvatar(spaceId, ((SuccessUploadFile)own).blobId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((avatar as FailedUploadFile)?.error, Is.EqualTo(UploadFileError.NOT_AUTHORIZED));
            Assert.That((header as FailedUploadFile)?.error, Is.EqualTo(UploadFileError.NOT_AUTHORIZED));
            Assert.That(completed, Is.EqualTo(new FailedSpaceManage(SpaceManageError.NO_PERMISSION)));
        });

        Assert.That((await SpaceAsync(owner, spaceId, ct)).avatarFieldId, Is.Null.Or.Empty);
    }
}
