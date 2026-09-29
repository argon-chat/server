namespace ArgonComplexTest.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// What an account can change about itself and what the server records about it: the profile edit
/// rules, the legal versions, an avatar replaced, a password digest moved onto the current scheme,
/// and the lockdown a sign-in is told about.
/// </summary>
[TestFixture]
public class ProfileEditTests : TestBase
{
    private static UserEditInput Edit(
        string? displayName = null, string? avatarId = null, string? customStatus = null, string? customStatusIconId = null,
        int? primaryColor = null, int? accentColor = null)
        => new(displayName, avatarId, null, null, null, null, customStatus, customStatusIconId, primaryColor, accentColor, null);

    private static IUserGrain UserGrain(Guid userId) => SocialHarness.Grains.GetGrain<IUserGrain>(userId);

    private static UpdateMeError? ErrorOf(IUpdateMeResult result) => (result as FailedUpdateMe)?.error;

    // ── Profile fields ──────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Colours_and_a_custom_status_need_ultima(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        var colour = await alice.Users.UpdateMe(Edit(primaryColor: 0x112233), ct);
        var status = await alice.Users.UpdateMe(Edit(customStatus: "busy"), ct);
        var both   = await alice.Users.UpdateMe(Edit(displayName: "Renamed", accentColor: 0x445566), ct);

        var profile = await alice.Users.GetMyProfile(ct);
        var me      = await alice.Users.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(colour), Is.EqualTo(UpdateMeError.PREMIUM_REQUIRED));
            Assert.That(ErrorOf(status), Is.EqualTo(UpdateMeError.PREMIUM_REQUIRED));
            Assert.That(ErrorOf(both), Is.EqualTo(UpdateMeError.PREMIUM_REQUIRED));
            Assert.That((profile.primaryColor, profile.accentColor, profile.customStatus), Is.EqualTo(((int?)null, (int?)null, (string?)null)));
            Assert.That(me.displayName, Is.Not.EqualTo("Renamed"), "a refused edit must not apply its free half either");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_ultima_account_sets_colours_and_a_status_capped_at_128_characters(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        await AccountSeed.SetUltimaAsync(alice.UserId, true, ct);

        var status = new string('s', 200);
        var result = await alice.Users.UpdateMe(Edit(customStatus: status, customStatusIconId: "icon-7", primaryColor: 0x112233, accentColor: 0x445566), ct);

        Assert.That(result, Is.InstanceOf<SuccessUpdateMe>(), $"refused: {ErrorOf(result)}");

        var profile = await alice.Users.GetMyProfile(ct);

        Assert.Multiple(() =>
        {
            Assert.That(profile.primaryColor, Is.EqualTo(0x112233));
            Assert.That(profile.accentColor, Is.EqualTo(0x445566));
            Assert.That(profile.customStatus, Is.EqualTo(status[..128]));
            Assert.That(profile.customStatusIconId, Is.EqualTo("icon-7"));
            Assert.That(((SuccessUpdateMe)result).profile.customStatus, Is.EqualTo(status[..128]), "the answer must match what was stored");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_display_name_is_trimmed_bounded_and_rate_limited(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        var blank   = await alice.Users.UpdateMe(Edit(displayName: "    "), ct);
        var tooLong = await alice.Users.UpdateMe(Edit(displayName: new string('n', 33)), ct);
        var renamed = await alice.Users.UpdateMe(Edit(displayName: "  Alice Prime  "), ct);
        var again   = await alice.Users.UpdateMe(Edit(displayName: "Alice Again"), ct);

        var me = await alice.Users.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(blank), Is.EqualTo(UpdateMeError.DISPLAY_NAME_EMPTY));
            Assert.That(ErrorOf(tooLong), Is.EqualTo(UpdateMeError.DISPLAY_NAME_TOO_LONG));
            Assert.That((renamed as SuccessUpdateMe)?.user.displayName, Is.EqualTo("Alice Prime"));
            Assert.That(ErrorOf(again), Is.EqualTo(UpdateMeError.COOLDOWN_ACTIVE), "a second rename inside the cooldown went through");
            Assert.That(me.displayName, Is.EqualTo("Alice Prime"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_avatar_id_that_names_no_file_is_refused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        var bare   = await alice.Users.UpdateMe(Edit(avatarId: "not-a-file-id"), ct);
        var nested = await alice.Users.UpdateMe(Edit(avatarId: $"avatars/{alice.UserId}/not-a-file-id"), ct);
        var ghost  = await alice.Users.UpdateMe(Edit(avatarId: Guid.NewGuid().ToString()), ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(ErrorOf(bare), Is.EqualTo(UpdateMeError.INVALID_PRESET_ID));
            Assert.That(ErrorOf(nested), Is.EqualTo(UpdateMeError.INVALID_PRESET_ID));
            Assert.That(ErrorOf(ghost), Is.EqualTo(UpdateMeError.INVALID_PRESET_ID));
            Assert.That((await alice.Users.GetMe(ct)).avatarFileId, Is.Null.Or.Empty);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Accepting_new_legal_versions_is_recorded(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        var accepted = await alice.Users.AcceptLegal(new AcceptLegalInput("tos-2026-09", "privacy-2026-10"), ct);
        var readBack = await alice.Users.GetMyLegalState(ct);

        Assert.Multiple(() =>
        {
            Assert.That((accepted.tosVersion, accepted.privacyVersion), Is.EqualTo(("tos-2026-09", "privacy-2026-10")));
            Assert.That((readBack.tosVersion, readBack.privacyVersion), Is.EqualTo(("tos-2026-09", "privacy-2026-10")));
        });
    }

    // ── Status emoji ────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_unicode_status_icon_is_stored_as_sent_and_checked(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        await AccountSeed.SetUltimaAsync(alice.UserId, true, ct);

        var set     = await alice.Users.UpdateMe(Edit(customStatus: "on fire", customStatusIconId: "🔥"), ct);
        var colon   = await alice.Users.UpdateMe(Edit(customStatusIconId: "a:b"), ct);
        var tooLong = await alice.Users.UpdateMe(Edit(customStatusIconId: new string('x', 17)), ct);
        var badId   = await alice.Users.UpdateMe(Edit(customStatusIconId: "ce:not-a-guid"), ct);
        var free    = await bob.Users.UpdateMe(Edit(customStatusIconId: "🔥"), ct);

        var profile = await alice.Users.GetMyProfile(ct);

        Assert.Multiple(() =>
        {
            Assert.That(set, Is.InstanceOf<SuccessUpdateMe>(), $"refused: {ErrorOf(set)}");
            Assert.That(((SuccessUpdateMe)set).profile.customStatusIconId, Is.EqualTo("🔥"));
            Assert.That(ErrorOf(colon), Is.EqualTo(UpdateMeError.INVALID_STATUS_EMOJI));
            Assert.That(ErrorOf(tooLong), Is.EqualTo(UpdateMeError.INVALID_STATUS_EMOJI));
            Assert.That(ErrorOf(badId), Is.EqualTo(UpdateMeError.INVALID_STATUS_EMOJI));
            Assert.That(ErrorOf(free), Is.EqualTo(UpdateMeError.PREMIUM_REQUIRED));
            Assert.That((profile.customStatus, profile.customStatusIconId), Is.EqualTo(("on fire", "🔥")));
            Assert.That(profile.customStatusEmoji, Is.Null);
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_custom_emoji_status_is_resolved_from_the_item_on_every_read(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var carol = await CreateSessionAsync(ct);
        await AccountSeed.SetUltimaAsync(alice.UserId, true, ct);

        var (spaceId, item) = await SpaceWithEmojiAsync(alice, "party", ct);
        await ChannelTestKit.JoinAsync(alice, carol, spaceId, ct);

        var set    = await alice.Users.UpdateMe(Edit(customStatus: "partying", customStatusIconId: $"ce:{item.itemId}"), ct);
        var own    = await alice.Users.GetMyProfile(ct);
        var seen   = (await carol.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([alice.UserId]), ct)).Values.Single();
        var stored = await StoredIconAsync(alice.UserId, ct);

        Assert.That(await ExpressionsOf(alice).UpdateItem(spaceId, item.itemId,
            new IonPartial<ExpressionItem>().Modify(x => x.name, "fiesta"), ct), Is.InstanceOf<SuccessItem>(), "setup: not renamed");

        var renamed  = await alice.Users.GetMyProfile(ct);
        var cleared  = await alice.Users.UpdateMe(Edit(customStatus: ""), ct);
        var after    = await alice.Users.GetMyProfile(ct);
        var expected = new StatusEmoji(item.itemId, spaceId, item.fileId, item.format, "party");

        Assert.Multiple(() =>
        {
            Assert.That(set, Is.InstanceOf<SuccessUpdateMe>(), $"refused: {ErrorOf(set)}");
            Assert.That(((SuccessUpdateMe)set).profile.customStatusEmoji, Is.EqualTo(expected));
            Assert.That(own.customStatusEmoji, Is.EqualTo(expected));
            Assert.That(own.customStatusIconId, Is.EqualTo($"ce:{item.itemId}"));
            Assert.That(seen.customStatusEmoji, Is.EqualTo(expected), "a member list does not see the emoji");
            Assert.That(stored, Is.EqualTo($"ce:{item.itemId}"), "the profile row keeps more than the reference");
            Assert.That(renamed.customStatusEmoji?.name, Is.EqualTo("fiesta"), "a renamed item is still read under its old name");

            Assert.That(cleared, Is.InstanceOf<SuccessUpdateMe>(), $"refused: {ErrorOf(cleared)}");
            Assert.That((after.customStatusIconId, after.customStatusEmoji), Is.EqualTo(((string?)null, (StatusEmoji?)null)),
                "clearing the status left its emoji behind");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_custom_emoji_is_taken_only_live_and_from_a_space_the_caller_is_in(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        await AccountSeed.SetUltimaAsync(bob.UserId, true, ct);

        var (spaceId, item) = await SpaceWithEmojiAsync(alice, "wave", ct);

        var outsider = await bob.Users.UpdateMe(Edit(customStatus: "hi", customStatusIconId: $"ce:{item.itemId}"), ct);
        var unknown  = await bob.Users.UpdateMe(Edit(customStatus: "hi", customStatusIconId: $"ce:{Guid.NewGuid()}"), ct);
        var before   = await bob.Users.GetMyProfile(ct);

        await ChannelTestKit.JoinAsync(alice, bob, spaceId, ct);
        var member = await bob.Users.UpdateMe(Edit(customStatus: "hi", customStatusIconId: $"ce:{item.itemId}"), ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(outsider), Is.EqualTo(UpdateMeError.INVALID_STATUS_EMOJI), "an emoji from a space bob is not in was taken");
            Assert.That(ErrorOf(unknown), Is.EqualTo(UpdateMeError.INVALID_STATUS_EMOJI));
            Assert.That((before.customStatus, before.customStatusEmoji), Is.EqualTo(((string?)null, (StatusEmoji?)null)),
                "a refused edit applied its status");
            Assert.That(member, Is.InstanceOf<SuccessUpdateMe>(), $"a member was refused: {ErrorOf(member)}");
            Assert.That((member as SuccessUpdateMe)?.profile.customStatusEmoji?.itemId, Is.EqualTo(item.itemId));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_custom_emoji_status_goes_with_the_item(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var carol = await CreateSessionAsync(ct);
        await AccountSeed.SetUltimaAsync(alice.UserId, true, ct);

        var (spaceId, item) = await SpaceWithEmojiAsync(alice, "gone", ct);
        await ChannelTestKit.JoinAsync(alice, carol, spaceId, ct);

        // Read once first, so the item is cached when it is deleted.
        var set = await alice.Users.UpdateMe(Edit(customStatus: "brb", customStatusIconId: $"ce:{item.itemId}"), ct);
        Assert.That((set as SuccessUpdateMe)?.profile.customStatusEmoji?.itemId, Is.EqualTo(item.itemId), $"setup: refused: {ErrorOf(set)}");

        Assert.That(await ExpressionsOf(alice).DeleteItem(spaceId, item.itemId, ct), Is.InstanceOf<SuccessItem>(), "setup: the item was not deleted");

        var seen  = (await carol.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([alice.UserId]), ct)).Values.Single();
        var own   = await alice.Users.GetMyProfile(ct);
        var again = await alice.Users.UpdateMe(Edit(customStatus: "brb", customStatusIconId: $"ce:{item.itemId}"), ct);

        var stored = await StoredIconAsync(alice.UserId, ct);
        for (var i = 0; stored is not null && i < 50; i++)
        {
            await Task.Delay(100, ct);
            stored = await StoredIconAsync(alice.UserId, ct);
        }

        Assert.Multiple(() =>
        {
            Assert.That((seen.customStatusIconId, seen.customStatusEmoji), Is.EqualTo(((string?)null, (StatusEmoji?)null)),
                "a member list still sees the deleted emoji");
            Assert.That((own.customStatusIconId, own.customStatusEmoji), Is.EqualTo(((string?)null, (StatusEmoji?)null)),
                "the status kept the deleted emoji");
            Assert.That(own.customStatus, Is.EqualTo("brb"), "the text went with the emoji");
            Assert.That(stored, Is.Null, "the reference to the deleted item was never forgotten");
            Assert.That(ErrorOf(again), Is.EqualTo(UpdateMeError.INVALID_STATUS_EMOJI), "a deleted item was set as a status emoji");
        });
    }

    private static async Task<string?> StoredIconAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);
        return await db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.CustomStatusIconId).SingleAsync(ct);
    }

    private static ISpaceExpressionInteraction ExpressionsOf(TestUserSession session)
        => session.Client.ForService<ISpaceExpressionInteraction>(ChannelTestKit.Services);

    private static async Task<(Guid SpaceId, ExpressionItem Item)> SpaceWithEmojiAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var spaceId     = await ChannelTestKit.CreateSpaceAsync(owner, ct);
        var expressions = ExpressionsOf(owner);

        var pack = await expressions.CreatePack(spaceId, ExpressionKind.Emoji, "Status", "status", ct);
        Assert.That(pack, Is.InstanceOf<SuccessPack>(), $"setup: no pack: {(pack as FailedPack)?.error}");

        var png    = EmojiPng();
        var begun  = await expressions.BeginUploadExpression(spaceId, ExpressionKind.Emoji, ExpressionFormat.Static, "image/png", png.Length, ct);
        Assert.That(begun, Is.InstanceOf<SuccessUploadFile>(), $"setup: the upload was not signed: {(begun as FailedUploadFile)?.error}");
        var ticket = (SuccessUploadFile)begun;

        using (var response = await TestObjectStore.UploadAsync(ticket.uploadUrl, png, "image/png", ticket.formFields.Values.Select(f => (f.key, f.value))))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "setup: the object store refused the upload");

        var added = await expressions.AddItem(spaceId, ((SuccessPack)pack).pack.packId, ticket.blobId, null, name,
            new IonArray<string>(["🎉"]), IonArray<string>.Empty, null, ct);
        Assert.That(added, Is.InstanceOf<SuccessItem>(), $"setup: no emoji: {(added as FailedItem)?.error}");

        return (spaceId, ((SuccessItem)added).item);
    }

    private static byte[] EmojiPng()
    {
        using var image = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        for (var y = 20; y < 80; y++)
        for (var x = 20; x < 80; x++)
            image[x, y] = new Rgba32(220, 40, 90, 255);

        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    // ── Avatar ──────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task Replacing_an_avatar_releases_the_old_file_and_tells_the_accounts_spaces(CancellationToken ct = default)
    {
        var alice   = await CreateSessionAsync(ct);
        var spaceId = ((SuccessCreateSpace)await alice.Users.CreateSpace(new CreateServerRequest("Avatars", "", ""), ct)).space.spaceId;

        await using var stream = await SocialHarness.OnlineAsync(alice, ct);

        var first  = await UploadAvatarAsync(alice, ct);
        var second = await UploadAvatarAsync(alice, ct);

        var me = await alice.Users.GetMe(ct);

        await stream.WaitForRecordAsync<UserUpdated>(
            e => e.spaceId == spaceId && e.dto.avatarFileId == me.avatarFileId, TimeSpan.FromSeconds(10), ct: ct);

        await using var db = await SocialHarness.DbAsync(ct);
        var counters = await db.FileCounters.AsNoTracking()
           .Where(c => c.Id == first || c.Id == second)
           .ToDictionaryAsync(c => c.Id, c => c.RefCount, ct);

        Assert.Multiple(() =>
        {
            Assert.That(me.avatarFileId, Does.EndWith(second.ToString()));
            Assert.That(counters[first], Is.Zero, "the replaced avatar still holds its reference, so it is never collected");
            Assert.That(counters[second], Is.EqualTo(1));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task An_avatar_the_classifier_rejects_is_released_recorded_and_not_applied(CancellationToken ct = default)
    {
        var alice  = await CreateSessionAsync(ct);
        var kept   = await UploadAvatarAsync(alice, ct);
        var ticket = (SuccessUploadFile)await alice.Users.BeginUploadAvatar(ct);

        Guid   rejected;
        string key;
        await using (var db = await SocialHarness.DbAsync(ct))
        {
            rejected = await db.FileBlobs.Where(b => b.Id == ticket.blobId).Select(b => b.FileId).SingleAsync(ct);
            key      = await db.KeyOfAsync(rejected, ct);
        }

        FactoryAsp.Services.GetRequiredService<FakeContentModeration>()
           .Deny(key, new Dictionary<string, float> { ["explicit"] = 0.97f }, new Dictionary<string, float> { ["explicit"] = 0.99f });

        await SocialHarness.UploadAsync(ticket, SocialHarness.Png, "image/png");

        Assert.That(async () => await alice.Users.CompleteUploadAvatar(ticket.blobId, ct), Throws.Exception,
            "a rejected avatar was reported as accepted");

        await using var read = await SocialHarness.DbAsync(ct);
        var counter   = await read.FileCounters.AsNoTracking().Where(c => c.Id == rejected).Select(c => c.RefCount).SingleAsync(ct);
        var violation = await read.ContentViolations.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == alice.UserId, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await alice.Users.GetMe(ct)).avatarFileId, Does.EndWith(kept.ToString()), "the rejected upload replaced the avatar");
            Assert.That(counter, Is.Zero, "the rejected file still holds a reference, so it is never collected");
            Assert.That(violation, Is.Not.Null, "the rejection left no record");
            Assert.That(violation?.FileId, Is.EqualTo(rejected));
            Assert.That(violation?.FilePurpose, Is.EqualTo(FilePurpose.Avatar));
            Assert.That(violation?.StagesUsed, Is.EqualTo(2));
            Assert.That(violation?.PrimaryScores, Does.ContainKey("explicit"));
            Assert.That(violation?.RefinedScores, Does.ContainKey("explicit"));
        });
    }

    private static async Task<Guid> UploadAvatarAsync(TestUserSession session, CancellationToken ct)
    {
        var begin = await session.Users.BeginUploadAvatar(ct);
        Assert.That(begin, Is.InstanceOf<SuccessUploadFile>(), $"refused: {(begin as FailedUploadFile)?.error}");

        var ticket = (SuccessUploadFile)begin;
        await SocialHarness.UploadAsync(ticket, SocialHarness.Png, "image/png");
        await session.Users.CompleteUploadAvatar(ticket.blobId, ct);

        await using var db = await SocialHarness.DbAsync(ct);
        return await db.Files.Where(f => f.OwnerId == session.UserId && f.Finalized)
           .OrderByDescending(f => f.CreatedAt).Select(f => f.Id).FirstAsync(ct);
    }

    // ── Sign-in side ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An account still on the original unsalted SHA-256 is moved onto the current scheme by its next
    /// successful sign-in, and can sign in with the same password afterwards.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_legacy_password_digest_is_upgraded_by_the_next_sign_in(CancellationToken ct = default)
    {
        var alice  = await CreateSessionAsync(ct);
        var legacy = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(alice.Credentials.password)));

        await using (var db = await SocialHarness.DbAsync(ct))
            await db.Users.Where(u => u.Id == alice.UserId)
               .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordDigest, legacy), ct);

        var credentials = new UserCredentialsInput(alice.Credentials.email, null, null, alice.Credentials.password, null, null);

        Assert.That(await GetIdentityService().Authorize(credentials, ct), Is.InstanceOf<SuccessAuthorize>(),
            "the legacy digest no longer signs in");

        var upgraded = (await AccountSeed.ReadUserAsync(alice.UserId, ct))!.PasswordDigest;

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(upgraded, Does.StartWith("$"), "the digest is still on the legacy scheme");
            Assert.That(await GetIdentityService().Authorize(credentials, ct), Is.InstanceOf<SuccessAuthorize>(),
                "the upgraded digest does not verify the password it was made from");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Upgrading_the_digest_of_an_account_that_does_not_exist_is_a_no_op(CancellationToken ct = default)
    {
        Assert.That(async () => await UserGrain(Guid.NewGuid()).UpgradePasswordDigest("$pbkdf2-sha512$i=1$AA$AA"), Throws.Nothing);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_lockdown_is_reported_with_its_severity_until_it_expires(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var until = DateTimeOffset.UtcNow.AddDays(3);

        async Task<LockedAuthStatus> LockAsync(LockdownReason reason, DateTimeOffset? expiration, bool appealable)
        {
            await using var db = await SocialHarness.DbAsync(ct);
            await db.Users.Where(u => u.Id == alice.UserId).ExecuteUpdateAsync(s => s
               .SetProperty(u => u.LockdownReason, reason)
               .SetProperty(u => u.LockDownExpiration, expiration)
               .SetProperty(u => u.LockDownIsAppealable, appealable), ct);

            return await UserGrain(alice.UserId).GetLimitationForUser();
        }

        var timed     = await LockAsync(LockdownReason.UNDER_INVESTIGATION, until, appealable: true);
        var permanent = await LockAsync(LockdownReason.CSAM, null, appealable: false);
        var lapsed    = await LockAsync(LockdownReason.INCITING_MOMENT, DateTimeOffset.UtcNow.AddMinutes(-1), appealable: true);

        Assert.Multiple(() =>
        {
            Assert.That(timed.lockdownReason, Is.EqualTo(LockdownReason.UNDER_INVESTIGATION));
            Assert.That(timed.lockDownExpiration, Is.EqualTo(until).Within(TimeSpan.FromSeconds(1)));
            Assert.That(timed.isAppealable, Is.True);
            Assert.That(timed.severity, Is.EqualTo(LockdownSeverity.Middle));

            Assert.That(permanent.lockdownReason, Is.EqualTo(LockdownReason.CSAM));
            Assert.That(permanent.lockDownExpiration, Is.GreaterThan(DateTimeOffset.UtcNow.AddYears(19)), "a lockdown without an end is reported as open-ended");
            Assert.That(permanent.isAppealable, Is.False);
            Assert.That(permanent.severity, Is.EqualTo(LockdownSeverity.Critical));

            Assert.That(lapsed.lockdownReason, Is.Null, "an expired lockdown is no lockdown");
            Assert.That(lapsed.severity, Is.EqualTo(LockdownSeverity.Low));
        });
    }
}
