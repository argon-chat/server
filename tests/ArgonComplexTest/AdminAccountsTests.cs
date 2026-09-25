namespace ArgonComplexTest.Tests;

using System.Security.Claims;
using System.Security.Cryptography;
using Argon.Entities;
using Argon.Features.Jwt;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using ConsoleContracts;
using ion.runtime;
using ion.runtime.client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the console does to one account: finding it, editing it, locking it, barring its machines,
/// reading its payments, and weighing what erasing it would take.
/// </summary>
/// <remarks>
/// A block or a device ban is checked by its effect on the account's next request, not by the row the
/// console wrote: the request pipeline reads both through a cache, and a write the cache does not hear
/// about is a ban that takes effect "eventually".
/// </remarks>
[TestFixture]
public class AdminAccountsTests : AdminTestBase
{
    protected override Guid SystemOperatorId  => Guid.Parse("00000000-0000-0000-0000-0000000ad201");
    protected override Guid RegularOperatorId => Guid.Parse("00000000-0000-0000-0000-0000000ad202");

    private IAdminUsersGrain Users => GetGrainFactory().GetGrain<IAdminUsersGrain>(Guid.Empty);

    private async Task<(Guid SpaceId, Guid ChannelId)> RoomAsync(TestUserSession owner, CancellationToken ct)
    {
        var created = await owner.Users.CreateSpace(new CreateServerRequest("Admin accounts", "fixture", string.Empty), ct);
        var spaceId = ((SuccessCreateSpace)created).space.spaceId;

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, "talk", ChannelType.Text, "fixture", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);

        return (spaceId, channels.Values.Single(c => c.channel.name == "talk").channel.channelId);
    }

    private static Task<long> SayAsync(TestUserSession who, (Guid SpaceId, Guid ChannelId) room, string text, CancellationToken ct)
        => who.Channels.SendMessage(room.SpaceId, room.ChannelId, text, new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

    // ── Finding and editing an account ──────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SearchUser_finds_a_phone_number_and_a_username_in_any_case_but_not_an_unknown_id(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var phone  = $"+1555{Random.Shared.Next(1_000_000, 9_999_999)}{Random.Shared.Next(100, 999)}";

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == person.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PhoneNumber, phone), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var byPhone    = await admin.SearchUser($"  {phone} ", ct);
        var byUsername = await admin.SearchUser(person.Credentials.username.ToUpperInvariant(), ct);
        var unknownId  = await admin.SearchUser(Guid.NewGuid().ToString(), ct);

        Assert.Multiple(() =>
        {
            Assert.That((byPhone.found, byPhone.userId, byPhone.matchedBy), Is.EqualTo((true, (Guid?)person.UserId, SearchMatchKind.Phone)));
            Assert.That((byUsername.userId, byUsername.matchedBy), Is.EqualTo(((Guid?)person.UserId, SearchMatchKind.Username)));
            Assert.That((unknownId.found, unknownId.matchedBy), Is.EqualTo((false, SearchMatchKind.None)),
                "an id nobody holds fell through to the name searches and matched something");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Account_edits_refuse_an_unknown_account_and_an_address_somebody_else_holds(CancellationToken ct = default)
    {
        var first  = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);
        var nobody = Guid.NewGuid();

        var (scope, admin) = Admin();
        await using var _ = scope;

        var results = new Dictionary<string, UserActionResult>
        {
            ["ChangeUsername"]      = await admin.ChangeUsername(nobody, $"ghost{Guid.NewGuid():N}"[..20], ct),
            ["ChangeEmail"]         = await admin.ChangeEmail(nobody, $"ghost_{Guid.NewGuid():N}@test.local", ct),
            ["RemoveTwoFactor"]     = await admin.RemoveTwoFactor(nobody, ct),
            ["RemovePhoneNumber"]   = await admin.RemovePhoneNumber(nobody, ct),
            ["ChangeUserAuthMode"]  = await admin.ChangeUserAuthMode(nobody, ArgonAuthMode.EmailOtp, ct),
            ["ChangeUserOtpMethod"] = await admin.ChangeUserOtpMethod(nobody, OtpMethod.Email, ct)
        };

        var taken = await admin.ChangeEmail(first.UserId, second.Credentials.email.ToUpperInvariant(), ct);

        await using var db = await NewDbAsync(ct);
        var firstRow = await db.Users.AsNoTracking().SingleAsync(u => u.Id == first.UserId, ct);

        Assert.Multiple(async () =>
        {
            foreach (var (action, result) in results)
            {
                Assert.That((result.success, result.error), Is.EqualTo((false, "User not found")), action);
                Assert.That(await AuditAsync(admin, action, nobody.ToString(), ct), Is.Empty, $"a refused {action} was audited");
            }

            Assert.That((taken.success, taken.error), Is.EqualTo((false, "Email already taken")),
                "two accounts would share one address, which is what sign-in resolves them by");
            Assert.That(firstRow.Email, Is.EqualTo(first.Credentials.email));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RemoveTwoFactor_and_RemovePhoneNumber_clear_what_the_account_had(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var phone  = $"+1666{Random.Shared.Next(1_000_000, 9_999_999)}{Random.Shared.Next(100, 999)}";

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == person.UserId).ExecuteUpdateAsync(s => s
               .SetProperty(u => u.PhoneNumber, phone)
               .SetProperty(u => u.TotpSecret, "JBSWY3DPEHPK3PXP"), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var before = await admin.GetUserCard(person.UserId, ct);

        var twoFactor = await admin.RemoveTwoFactor(person.UserId, ct);
        var phoneGone = await admin.RemovePhoneNumber(person.UserId, ct);

        var after = await admin.GetUserCard(person.UserId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((before.hasTwoFactor, before.account.phoneNumber), Is.EqualTo((true, (string?)phone)), "premise");
            Assert.That(twoFactor.success && phoneGone.success, Is.True, $"{twoFactor.error} {phoneGone.error}");
            Assert.That(after.hasTwoFactor, Is.False);
            Assert.That(after.account.phoneNumber, Is.Null);
            Assert.That(await AuditAsync(admin, "RemoveTwoFactor", person.UserId.ToString(), ct), Has.Count.EqualTo(1));
            Assert.That(await AuditAsync(admin, "RemovePhoneNumber", person.UserId.ToString(), ct), Has.Count.EqualTo(1));
        });
    }

    // ── Lockdown ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A block stops the account's very next post, and lifting it lets the next one through.
    /// </summary>
    /// <remarks>
    /// The request pipeline reads the account's lockdown through a cache it holds for half a minute, so
    /// this is what pins the write dropping that entry beside it. The first post is what primes the
    /// cache with "not locked"; without the drop the block would be invisible to it for the rest of
    /// that window.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_block_stops_the_very_next_post_and_lifting_it_lets_the_next_one_through(CancellationToken ct = default)
    {
        var member = await CreateSessionAsync(ct);
        var room   = await RoomAsync(member, ct);

        await SayAsync(member, room, "before the block", ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var blocked = await admin.BlockUser(member.UserId, LockdownReason.SPAM_SCAM_ACCOUNT, null, true, ct);

        Assert.That(blocked.success, Is.True, blocked.error);
        Assert.That(async () => await SayAsync(member, room, "while blocked", ct), Throws.InstanceOf<IonRequestException>(),
            "a blocked account went on posting on its cached clean record");

        var lifted = await admin.UnblockUser(member.UserId, ct);

        Assert.That(lifted.success, Is.True, lifted.error);
        Assert.That(await SayAsync(member, room, "after the block", ct), Is.GreaterThan(0),
            "an unblocked account was still refused on its cached lockdown");
    }

    // ── Devices ─────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> EnrollDeviceAsync(Guid userId, CancellationToken ct)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var device = await GetGrainFactory().GetGrain<IDeviceIdentityGrain>(Guid.Empty)
           .ResolveByKeyAsync(userId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), ct);

        Assert.That(device, Is.Not.Null, "the machine could not be enrolled");

        return device!.Value;
    }

    /// <summary>
    /// The accounts on one machine, most recently seen first, and which of them are blocked right now.
    /// </summary>
    /// <remarks>
    /// A lockdown whose expiry has passed is no lockdown — the request pipeline serves the account as
    /// clean — so the list must not mark it as blocked. It used to compare the reason alone, and an
    /// operator weighing the collateral of a device ban was shown a lapsed timeout as a live block.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task GetDeviceAccounts_lists_everyone_on_the_machine_and_who_is_blocked_now(CancellationToken ct = default)
    {
        var clean   = await CreateSessionAsync(ct);
        var blocked = await CreateSessionAsync(ct);
        var lapsed  = await CreateSessionAsync(ct);

        var device = await EnrollDeviceAsync(clean.UserId, ct);
        var now    = DateTimeOffset.UtcNow;

        await using (var db = await NewDbAsync(ct))
        {
            db.DeviceObservations.AddRange(
                new DeviceObservationEntity
                {
                    Id = Guid.CreateVersion7(), UserId = blocked.UserId, DeviceId = device, Components = "1;mg:abc",
                    FirstSeenAt = now.AddDays(-3), LastSeenAt = now.AddHours(-1), Logins = 4
                },
                new DeviceObservationEntity
                {
                    Id = Guid.CreateVersion7(), UserId = lapsed.UserId, DeviceId = device, Components = "1;mg:abc",
                    FirstSeenAt = now.AddDays(-5), LastSeenAt = now.AddHours(-2), Logins = 2
                });

            await db.SaveChangesAsync(ct);

            await db.Users.Where(u => u.Id == blocked.UserId).ExecuteUpdateAsync(s => s
               .SetProperty(u => u.LockdownReason, LockdownReason.SPAM_SCAM_ACCOUNT)
               .SetProperty(u => u.LockDownExpiration, now.AddDays(1)), ct);
            await db.Users.Where(u => u.Id == lapsed.UserId).ExecuteUpdateAsync(s => s
               .SetProperty(u => u.LockdownReason, LockdownReason.INCITING_MOMENT)
               .SetProperty(u => u.LockDownExpiration, now.AddDays(-1)), ct);
        }

        var (scope, admin) = Admin();
        await using var _ = scope;

        var accounts = (await admin.GetDeviceAccounts(device, ct)).accounts.Values.ToList();
        var empty    = await admin.GetDeviceAccounts(Guid.NewGuid(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(accounts.Select(a => a.userId), Is.EqualTo(new[] { clean.UserId, blocked.UserId, lapsed.UserId }),
                "most recently seen first");
            Assert.That(accounts.Select(a => a.username),
                Is.EqualTo(new[] { clean.Credentials.username, blocked.Credentials.username, lapsed.Credentials.username }));
            Assert.That(accounts.Select(a => a.logins), Is.EqualTo(new[] { 1, 4, 2 }));
            Assert.That(accounts.Select(a => a.isBlocked), Is.EqualTo(new[] { false, true, false }),
                "a lockdown that has run out is shown as a live block");
            Assert.That(empty.accounts.Size, Is.EqualTo(0));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task BanDevice_refuses_a_machine_it_cannot_recognise_and_revives_an_earlier_ban(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var device = await EnrollDeviceAsync(owner.UserId, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        async Task<bool> IsBannedAsync()
            => (await admin.GetUserDevices(owner.UserId, ct)).devices.Values.Single(d => d.deviceId == device).isBanned;

        var unknown   = await admin.BanDevice(Guid.NewGuid(), "never seen", null, ct);
        var notBanned = await admin.UnbanDevice(device, ct);

        var first         = await admin.BanDevice(device, "first", null, ct);
        var afterFirst    = await IsBannedAsync();
        var unbanned      = await admin.UnbanDevice(device, ct);
        var afterUnban    = await IsBannedAsync();
        var expiry        = DateTimeOffset.UtcNow.AddDays(3);
        var second        = await admin.BanDevice(device, "second", expiry, ct);
        var afterSecond   = await IsBannedAsync();

        await using var db = await NewDbAsync(ct);
        var rows = await db.DeviceBans.IgnoreQueryFilters().AsNoTracking().Where(b => b.DeviceId == device).ToListAsync(ct);

        Assert.Multiple(async () =>
        {
            Assert.That((unknown.success, unknown.error), Is.EqualTo((false, "Device not found")),
                "a ban on a machine the server cannot recognise on sight does nothing");
            Assert.That((notBanned.success, notBanned.error), Is.EqualTo((false, "Device is not banned")));

            Assert.That(first.success && unbanned.success && second.success, Is.True, $"{first.error} {unbanned.error} {second.error}");
            Assert.That((afterFirst, afterUnban, afterSecond), Is.EqualTo((true, false, true)));

            Assert.That(rows, Has.Count.EqualTo(1), "the second ban was inserted beside the first instead of reviving it");
            Assert.That(rows.Single().Reason, Is.EqualTo("second"));
            Assert.That(rows.Single().ExpiresAt, Is.EqualTo(expiry).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(rows.Single().IsDeleted, Is.False);

            Assert.That(await AuditAsync(admin, "BanDevice", device.ToString(), ct), Has.Count.EqualTo(2));
            Assert.That(await AuditAsync(admin, "UnbanDevice", device.ToString(), ct), Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    /// A banned machine is refused on its very next call, and served again on the next call after the
    /// ban is lifted.
    /// </summary>
    /// <remarks>
    /// <para>The request pipeline asks whether the machine a token is bound to is banned, and caches the
    /// answer for half a minute. The ban and the unban did not drop it, so a machine that had just made
    /// a request — which is every machine anybody is in a hurry to ban — kept being served on its cached
    /// "not banned" for that long, and an unbanned one stayed locked out. The interceptor's own comment
    /// is the requirement: a ban that takes effect "eventually" is not what anyone means by banning a
    /// machine.</para>
    ///
    /// <para>The bound session is minted by hand, the way the refresh path mints one for a machine that
    /// proved its key: the device id rides on the access token as <c>did</c>. The refusal is asserted as
    /// a refusal, not as <c>DEVICE_BANNED</c>: the transport re-reads every interceptor error as
    /// <c>UPSTREAM_ERROR</c> (see <c>AccountDeletionTests</c>), so the same account on an unbound
    /// session is the control that shows it is the machine being refused.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_banned_machine_is_refused_on_its_next_call_and_served_again_once_unbanned(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var device  = await EnrollDeviceAsync(account.UserId, ct);

        var interceptor = new DefaultHeaderInterceptor(Guid.CreateVersion7());
        var flow        = FactoryAsp.Services.GetRequiredService<ClassicJwtFlow>();

        interceptor.SetToken(flow.GenerateAccessToken(account.UserId, interceptor.MachineId, ["argon.app"],
            [new Claim("did", device.ToString())]));

        var client = IonClient.Create(HttpClient, WsFactory);
        client.WithInterceptor(interceptor);

        var bound = client.ForService<IUserInteraction>(FactoryAsp.Services);

        Assert.That((await bound.GetMe(ct)).userId, Is.EqualTo(account.UserId), "premise: the bound session is served");

        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That((await admin.BanDevice(device, "alt farm", null, ct)).success, Is.True);

        Assert.ThrowsAsync<IonRequestException>(async () => await bound.GetMe(ct),
            "the banned machine was served on its cached 'not banned'");
        Assert.That((await account.Users.GetMe(ct)).userId, Is.EqualTo(account.UserId),
            "the same account on an unbound session is refused too, so the refusal is not about the machine");

        Assert.That((await admin.UnbanDevice(device, ct)).success, Is.True);

        Assert.That((await bound.GetMe(ct)).userId, Is.EqualTo(account.UserId),
            "the unbanned machine was still refused on its cached ban");
    }

    // ── Payments ────────────────────────────────────────────────────────────────────────────────

    private async Task<PaymentTransactionEntity> SeedTransactionAsync(Guid userId, string status, CancellationToken ct)
    {
        var tx = new PaymentTransactionEntity
        {
            Id              = Guid.CreateVersion7(),
            UserId          = userId,
            XsollaTxId      = $"xs_{Guid.NewGuid():N}",
            TransactionType = "subscription",
            PlanExternalId  = "ultima_monthly",
            Amount          = "9.99",
            Currency        = "USD",
            CardSuffix      = "4242",
            CardBrand       = "Visa",
            Status          = status
        };

        await using var db = await NewDbAsync(ct);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync(ct);

        return tx;
    }

    [Test, CancelAfter(120_000)]
    public async Task GetTransactionByXsollaId_returns_the_payment_with_what_it_granted_around_it(CancellationToken ct = default)
    {
        var buyer = await CreateSessionAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var templateId = $"tx_{Guid.NewGuid():N}"[..24];
        var template   = await admin.CreateItemTemplate(new CreateItemTemplateInput(templateId, true, false, false,
            null, ItemScenarioKind.None, new IonArray<string>([])), ct);

        Assert.That(template.success, Is.True, template.error);

        // One item well before the payment, which is not what it bought; one beside it, which is.
        Assert.That((await admin.GrantItem(buyer.UserId, template.itemId!.Value, ct)).success, Is.True);

        await using (var db = await NewDbAsync(ct))
            await db.Items.Where(i => i.OwnerId == buyer.UserId && !i.IsReference)
               .ExecuteUpdateAsync(s => s.SetProperty(i => i.CreatedAt, DateTimeOffset.UtcNow.AddHours(-1)), ct);

        var tx = await SeedTransactionAsync(buyer.UserId, "done", ct);

        Assert.That((await admin.GrantItem(buyer.UserId, template.itemId!.Value, ct)).success, Is.True);
        Assert.That((await admin.GrantPremium(buyer.UserId, UltimaPlan.Monthly, 30, ct)).success, Is.True);

        var details = await admin.GetTransactionByXsollaId(tx.XsollaTxId, ct);

        await using var rows = await NewDbAsync(ct);
        var beside = await rows.Items.AsNoTracking()
           .Where(i => i.OwnerId == buyer.UserId && !i.IsReference && i.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-30))
           .Select(i => i.Id)
           .ToListAsync(ct);

        Assert.That(details, Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(details!.transaction.transactionId, Is.EqualTo(tx.Id));
            Assert.That(details.transaction.username, Is.EqualTo(buyer.Credentials.username));
            Assert.That((details.transaction.amount, details.transaction.currency, details.transaction.status),
                Is.EqualTo(("9.99", "USD", "done")));
            Assert.That(beside, Is.Not.Empty, "premise: something was granted beside the payment");
            Assert.That(details.relatedItems.Values.Select(i => i.itemId), Is.EquivalentTo(beside),
                "the related items are the ones granted around the payment, and only those");
            Assert.That(details.relatedItems.Values.Select(i => i.templateId), Has.Member(templateId));
            Assert.That(details.premiumInfo?.tier, Is.EqualTo(UltimaPlan.Monthly));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetUserTransactions_pages_newest_first_and_keeps_its_arguments_in_range(CancellationToken ct = default)
    {
        var buyer = await CreateSessionAsync(ct);

        var older = await SeedTransactionAsync(buyer.UserId, "done", ct);
        var newer = await SeedTransactionAsync(buyer.UserId, "refunded", ct);

        await using (var db = await NewDbAsync(ct))
            await db.PaymentTransactions.Where(t => t.Id == older.Id)
               .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, DateTimeOffset.UtcNow.AddDays(-1)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var clamped = await admin.GetUserTransactions(buyer.UserId, -4, 0, ct);
        var second  = await admin.GetUserTransactions(buyer.UserId, 1, 1, ct);
        var capped  = await admin.GetUserTransactions(buyer.UserId, 0, 5_000, ct);

        Assert.Multiple(() =>
        {
            Assert.That((clamped.page, clamped.pageSize, clamped.totalCount), Is.EqualTo((0, 1, 2)));
            Assert.That(clamped.transactions.Values.Single().transactionId, Is.EqualTo(newer.Id), "newest first");
            Assert.That(second.transactions.Values.Single().transactionId, Is.EqualTo(older.Id));
            Assert.That(capped.pageSize, Is.EqualTo(100));
            Assert.That(capped.transactions.Values.Select(t => t.status), Is.EqualTo(new[] { "refunded", "done" }));
        });
    }

    // ── Deletion impact ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The account's last activity is the later of its last sign-in and its last message, whichever
    /// of the two it has.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task GetAccountDeletionImpact_takes_the_later_of_the_last_sign_in_and_the_last_message(CancellationToken ct = default)
    {
        var talker   = await CreateSessionAsync(ct);
        var sleeper  = await CreateSessionAsync(ct);
        var returner = await CreateSessionAsync(ct);

        var room = await RoomAsync(talker, ct);
        await SayAsync(talker, room, "only ever wrote", ct);

        var returnerRoom = await RoomAsync(returner, ct);
        await SayAsync(returner, returnerRoom, "wrote after signing in", ct);

        var longAgo = DateTimeOffset.UtcNow.AddDays(-400);
        await AccountSeed.BackdateLastLoginAsync(sleeper.UserId, longAgo, ct: ct);
        await AccountSeed.BackdateLastLoginAsync(returner.UserId, longAgo, ct: ct);
        await AccountSeed.SetAutoDeleteAsync(returner.UserId, 6, true, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var talked   = await admin.GetAccountDeletionImpact(talker.UserId, ct);
        var slept    = await admin.GetAccountDeletionImpact(sleeper.UserId, ct);
        var returned = await admin.GetAccountDeletionImpact(returner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(talked.lastLoginAt, Is.Null, "premise: this account never signed in on record");
            Assert.That(talked.lastMessageAt, Is.Not.Null);
            Assert.That(talked.lastActivityAt, Is.EqualTo(talked.lastMessageAt));
            Assert.That(talked.messages, Is.EqualTo(1));

            Assert.That(slept.lastMessageAt, Is.Null);
            Assert.That(slept.lastActivityAt, Is.EqualTo(slept.lastLoginAt).And.EqualTo(longAgo).Within(TimeSpan.FromSeconds(1)));
            Assert.That((slept.thresholdMonths, slept.autoDeleteChosen), Is.EqualTo((12, false)), "the platform default");

            Assert.That(returned.lastActivityAt, Is.EqualTo(returned.lastMessageAt), "the message is the later of the two");
            Assert.That(returned.lastLoginAt, Is.LessThan(returned.lastMessageAt ?? DateTimeOffset.MinValue));
            Assert.That((returned.thresholdMonths, returned.autoDeleteChosen), Is.EqualTo((6, true)), "the account's own choice");
            Assert.That(returned.blockedBy, Is.Null, "nothing bars erasing an ordinary account");
        });
    }

    /// <summary>
    /// The panel names the first bar the deletion grain would refuse on, in the order it checks them.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task GetAccountDeletionImpact_names_the_bar_the_deletion_would_be_refused_on(CancellationToken ct = default)
    {
        var owner      = await CreateSessionAsync(ct);
        var locked     = await CreateSessionAsync(ct);
        var lapsed     = await CreateSessionAsync(ct);
        var subscriber = await CreateSessionAsync(ct);
        var scheduled  = await CreateSessionAsync(ct);

        var (botAppId, botUserId, _) = await SeedBotAsync(await SeedTeamAsync(owner.UserId, "Impact Team", ct), "Impact Bot", false, ct);

        await AccountSeed.LockAsync(locked.UserId, LockdownReason.SPAM_SCAM_ACCOUNT, ct);
        await AccountSeed.LockAsync(lapsed.UserId, LockdownReason.INCITING_MOMENT, ct);
        await AccountSeed.SetUltimaAsync(subscriber.UserId, true, ct);

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == lapsed.UserId)
               .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockDownExpiration, DateTimeOffset.UtcNow.AddHours(-1)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That((await admin.StartAccountDeletion(scheduled.UserId, ct)).success, Is.True);

        var bot            = await admin.GetAccountDeletionImpact(botUserId, ct);
        var lockedImpact   = await admin.GetAccountDeletionImpact(locked.UserId, ct);
        var lapsedImpact   = await admin.GetAccountDeletionImpact(lapsed.UserId, ct);
        var subscribed     = await admin.GetAccountDeletionImpact(subscriber.UserId, ct);
        var inGrace        = await admin.GetAccountDeletionImpact(scheduled.UserId, ct);
        var ownerImpact    = await admin.GetAccountDeletionImpact(owner.UserId, ct);

        // The deletion was started for the panel to read, not to run.
        await GetGrainFactory().GetGrain<IAccountDeletionGrain>(scheduled.UserId).CancelDeletionAsync();

        Assert.Multiple(() =>
        {
            Assert.That((bot.isBot, bot.blockedBy), Is.EqualTo((true, "bot or platform account")));
            Assert.That((lockedImpact.isLocked, lockedImpact.blockedBy), Is.EqualTo((true, "standing lockdown")));
            Assert.That((lapsedImpact.isLocked, lapsedImpact.blockedBy), Is.EqualTo((false, (string?)null)),
                "a lockdown that has run out bars nothing");
            Assert.That((subscribed.hasActiveSubscription, subscribed.blockedBy), Is.EqualTo((true, "active subscription")));
            Assert.That(inGrace.deletionStatus, Is.EqualTo(AccountDeletionStatusView.SCHEDULED));
            Assert.That(inGrace.blockedBy, Is.EqualTo("deletion already Scheduled"));
            Assert.That(inGrace.scheduledAt, Is.Not.Null);
            Assert.That(inGrace.executionAt, Is.GreaterThan(inGrace.scheduledAt ?? DateTimeOffset.MaxValue));
            Assert.That(ownerImpact.botsOwned, Is.EqualTo(1), $"the owner's team holds bot {botAppId}");
        });
    }

    /// <summary>
    /// An erasure that stopped part way is reported as failed, and as the bar to starting another.
    /// </summary>
    /// <remarks>
    /// The failure is made the way <c>AdminConsoleTests</c> makes it: another row holding the
    /// <c>deleted_{userId}@void.local</c> address the anonymising step writes violates the unique index
    /// before that step commits, so the account is still there to be read.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task GetAccountDeletionImpact_reports_an_erasure_that_stopped_part_way(CancellationToken ct = default)
    {
        var target = await CreateSessionAsync(ct);
        var decoy  = await CreateSessionAsync(ct);

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, $"deleted_{target.UserId}@void.local"), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var erased = await admin.EraseAccountNow(target.UserId, ct);
        var impact = await admin.GetAccountDeletionImpact(target.UserId, ct);

        // Clear the cause, so the erasure's own retry finishes it rather than leaving it stranded.
        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, decoy.Credentials.email), ct);

        Assert.Multiple(() =>
        {
            Assert.That(erased.success, Is.False, "premise: the erasure was meant to fail");
            Assert.That(impact.found, Is.True, "the account is still there, since the erasure stopped before hiding it");
            Assert.That(impact.deletionStatus, Is.EqualTo(AccountDeletionStatusView.FAILED));
            Assert.That(impact.blockedBy, Is.EqualTo("deletion already Failed"));
        });
    }

    /// <summary>
    /// The queue pages' name lookup reads erased accounts too, and asks nothing for an empty page.
    /// </summary>
    /// <remarks>
    /// Called straight on the grain: every console page returns before asking when it has no rows,
    /// so the empty case is the grain's own contract rather than something a page can reach.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task GetAccountIdentities_names_erased_accounts_and_answers_an_empty_request(CancellationToken ct = default)
    {
        var live = await CreateSessionAsync(ct);
        var gone = await CreateSessionAsync(ct);

        await using (var db = await NewDbAsync(ct))
            await db.Users.Where(u => u.Id == gone.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true), ct);

        var none  = await Users.GetAccountIdentitiesAsync([]);
        var found = await Users.GetAccountIdentitiesAsync([live.UserId, gone.UserId, Guid.NewGuid()]);

        Assert.Multiple(() =>
        {
            Assert.That(none, Is.Empty);
            Assert.That(found.Keys, Is.EquivalentTo(new[] { live.UserId, gone.UserId }));
            Assert.That(found[gone.UserId].Username, Is.EqualTo(gone.Credentials.username),
                "an erased account's row is still what the queue names it by");
            Assert.That(found[live.UserId].Email, Is.EqualTo(live.Credentials.email));
        });
    }
}
