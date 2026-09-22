namespace Argon.Grains;

using Argon.Api.Entities.Data;
using Argon.Core.Entities.Data;
using Argon.Features.Logic;
using Argon.Grains.Interfaces;
using Argon.Services.Ion;
using ConsoleContracts;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;

/// <inheritdoc cref="IAdminUsersGrain"/>
[StatelessWorker]
public sealed class AdminUsersGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    HybridCache cache,
    IOptions<AccountDeletionOptions> deletionOptions) : Grain, IAdminUsersGrain
{
    public async Task<SearchUserResult> SearchUserAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchUserResult(false, null, SearchMatchKind.None);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var normalizedQuery = query.Trim().ToLowerInvariant();

        // Try parse as GUID first
        if (Guid.TryParse(query, out var userId))
        {
            var exists = await db.Users.AnyAsync(u => u.Id == userId, ct);
            if (exists)
                return new SearchUserResult(true, userId, SearchMatchKind.UserId);
        }

        // Search by username
        var byUsername = await db.Users
           .Where(u => u.NormalizedUsername == normalizedQuery)
           .Select(u => u.Id)
           .FirstOrDefaultAsync(ct);
        if (byUsername != Guid.Empty)
            return new SearchUserResult(true, byUsername, SearchMatchKind.Username);

        // Search by email
        var byEmail = await db.Users
           .Where(u => u.NormalizedEmail == normalizedQuery)
           .Select(u => u.Id)
           .FirstOrDefaultAsync(ct);
        if (byEmail != Guid.Empty)
            return new SearchUserResult(true, byEmail, SearchMatchKind.Email);

        // Search by phone
        var byPhone = await db.Users
           .Where(u => u.PhoneNumber != null && u.PhoneNumber == query.Trim())
           .Select(u => u.Id)
           .FirstOrDefaultAsync(ct);
        if (byPhone != Guid.Empty)
            return new SearchUserResult(true, byPhone, SearchMatchKind.Phone);

        return new SearchUserResult(false, null, SearchMatchKind.None);
    }

    public async Task<UserCardDetails?> GetUserCardAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The account, and everything about it that is a single row or a single number, in one round trip.
        var head = await (
                from u in db.Users.AsNoTracking()
                where u.Id == userId
                join l in db.UserLevels on u.Id equals l.UserId into levels
                from levelRow in levels.DefaultIfEmpty()
                join a in db.AutoDeleteSettings on u.Id equals a.UserId into autoDeletes
                from autoDeleteRow in autoDeletes.DefaultIfEmpty()
                select new
                {
                    User           = u,
                    u.Profile,
                    BotVerified    = u.BotEntity != null && u.BotEntity.IsVerified,
                    Level          = levelRow,
                    AutoDelete     = autoDeleteRow,
                    LastLogin      = db.DeviceHistories.Where(d => d.UserId == u.Id).Max(d => d.LastLoginTime),
                    Blocked        = db.UserBlocklist.Count(b => b.UserId == u.Id),
                    DirectMessages = db.DirectMessages.Count(m => m.SenderId == u.Id),
                    Conversations  = db.Conversations.Count(c => c.Participant1Id == u.Id || c.Participant2Id == u.Id),
                    Passkeys       = db.Passkeys.Count(p => p.UserId == u.Id && p.IsCompleted),
                    Friends        = db.Friends.Count(f => f.UserId == u.Id),
                    VoiceSeconds   = db.UserDailyStats.Where(s => s.UserId == u.Id).Sum(s => s.TimeInVoiceSeconds),
                    CallsMade      = db.UserDailyStats.Where(s => s.UserId == u.Id).Sum(s => s.CallsMade),
                    MessagesSent   = db.UserDailyStats.Where(s => s.UserId == u.Id).Sum(s => s.MessagesSent),
                    XpEarned       = db.UserDailyStats.Where(s => s.UserId == u.Id).Sum(s => s.XpEarned)
                })
           .FirstOrDefaultAsync(ct);

        if (head is null)
            return null;

        var user = head.User;

        var account = new UserAccountInfo(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Email,
            user.PhoneNumber,
            user.AvatarFileId,
            user.DateOfBirth,
            user.CreatedAt.UtcDateTime,
            user.LockdownReason,
            user.LockDownExpiration?.UtcDateTime,
            user.LockDownIsAppealable,
            user.PreferredAuthMode,
            user.PreferredOtpMethod,
            user.AgreeTOS,
            head.LastLogin?.UtcDateTime,
            head.Blocked,
            head.DirectMessages,
            head.Conversations
        );

        var itemEntities = await db.Items
           .AsNoTracking()
           .Where(i => i.OwnerId == userId && !i.IsReference)
           .Include(i => i.Scenario)
           .ToListAsync(ct);

        var boxedItems = await AdminBoxContents.ReadAsync(db, itemEntities.Select(i => i.Scenario), ct);

        var items = itemEntities.Select(item => new InventoryItemInfo(
            item.Id,
            item.TemplateId,
            item.IsUsable,
            item.IsGiftable,
            item.ReceivedFrom,
            item.TTL.HasValue ? (int)item.TTL.Value.TotalSeconds : null,
            item.CreatedAt.UtcDateTime,
            item.Scenario is BoxScenario or QualifierBox or MultipleQualifierBox,
            AdminBoxContents.Of(item.Scenario, boxedItems)
        )).ToList();

        var messages = await db.Messages
           .AsNoTracking()
           .Where(m => m.CreatorId == userId)
           .OrderByDescending(m => m.CreatedAt)
           .Take(10)
           .Select(m => new
            {
                m.MessageId,
                m.SpaceId,
                m.ChannelId,
                m.Text,
                m.CreatedAt,
                SpaceName   = db.Spaces.Where(s => s.Id == m.SpaceId).Select(s => s.Name).FirstOrDefault(),
                ChannelName = db.Channels.Where(c => c.Id == m.ChannelId).Select(c => c.Name).FirstOrDefault()
            })
           .ToListAsync(ct);

        var recentMessages = messages.Select(m => new MessageInfo(
            m.MessageId,
            m.SpaceId,
            m.SpaceName ?? "Unknown",
            m.ChannelId,
            m.ChannelName ?? "Unknown",
            m.Text,
            m.CreatedAt.UtcDateTime
        )).ToArray();

        var deviceHistoryEntities = await db.DeviceHistories
           .AsNoTracking()
           .Where(d => d.UserId == userId)
           .OrderByDescending(d => d.LastLoginTime)
           .Take(10)
           .ToListAsync(ct);

        var deviceHistory = deviceHistoryEntities.Select(d => new DeviceHistoryInfo(
            d.MachineId,
            d.LastLoginTime?.UtcDateTime,
            d.LastKnownIP ?? "",
            d.RegionAddress ?? "",
            d.AppId ?? "",
            (ConsoleContracts.DeviceTypeKind)(int)d.DeviceType
        )).ToList();

        var redemptionRows = await db.CouponRedemption
           .AsNoTracking()
           .Where(r => r.UserId == userId)
           .Select(r => new { r.CouponId, r.Coupon.Code, r.RedeemedAt, Items = r.Items.Count })
           .ToListAsync(ct);

        var redemptions = redemptionRows
           .Select(r => new RedeemedCouponInfo(r.CouponId, r.Code, r.RedeemedAt.UtcDateTime, r.Items))
           .ToList();

        // The five most recent memberships, each with its space's head counts.
        var membershipRows = await db.UsersToServerRelations
           .AsNoTracking()
           .Where(sm => sm.UserId == userId && !sm.IsDeleted)
           .OrderByDescending(sm => sm.CreatedAt)
           .Take(5)
           .Select(sm => new
            {
                sm.SpaceId,
                sm.Space.Name,
                sm.Space.AvatarFileId,
                sm.Space.CreatorId,
                sm.CreatedAt,
                Members  = db.UsersToServerRelations.Count(x => x.SpaceId == sm.SpaceId && !x.IsDeleted),
                Channels = db.Channels.Count(c => c.SpaceId == sm.SpaceId)
            })
           .ToListAsync(ct);

        var spaces = membershipRows.Select(sm => new UserSpaceInfo(
            sm.SpaceId,
            sm.Name,
            sm.AvatarFileId,
            sm.Members,
            sm.Channels,
            sm.CreatedAt.UtcDateTime,
            sm.CreatorId == userId
        )).ToList();

        var level = head.Level is { } levelEntity
            ? new UserLevelInfo(
                levelEntity.CurrentLevel,
                levelEntity.CurrentCycleXp,
                levelEntity.TotalXpAllTime,
                levelEntity.CanClaimMedal,
                levelEntity.LastXpAward.UtcDateTime)
            : new UserLevelInfo(1, 0, 0, false, DateTime.UtcNow);

        var stats = new UserStatsInfo(head.VoiceSeconds, head.CallsMade, head.MessagesSent, head.XpEarned);

        // Teams (first 3)
        var teamMemberships = await db.MemberTeamEntities
           .AsNoTracking()
           .Where(tm => tm.UserId == userId)
           .Take(3)
           .Select(tm => new UserTeamInfo(
                tm.TeamId,
                tm.Team.Name,
                tm.Team.AvatarFileId,
                tm.IsOwner,
                tm.JoinedAt
            ))
           .ToListAsync(ct);

        var autoDeleteSettings = head.AutoDelete is { } autoDelete
            ? new AutoDeleteSettingsInfo(autoDelete.Enabled, autoDelete.Months)
            : null;

        // Bots owned by user (through team membership)
        var userBots = await db.BotEntities
           .AsNoTracking()
           .Where(b => db.MemberTeamEntities.Any(tm => tm.UserId == userId && tm.TeamId == b.TeamId))
           .Select(b => new AdminUserBotInfo(
                b.AppId,
                b.Name,
                b.BotAsUser.Username,
                b.IsVerified,
                b.LifecycleState == BotLifecycleState.Published,
                b.TeamId,
                b.Team.Name
            ))
           .ToListAsync(ct);

        var premiumInfo = await ReadPremiumAsync(db, userId, ct);

        return new UserCardDetails(
            account,
            head.Profile.ToDto(),
            head.Passkeys,
            !string.IsNullOrEmpty(user.TotpSecret),
            new IonArray<InventoryItemInfo>(items),
            new IonArray<MessageInfo>(recentMessages),
            new IonArray<DeviceHistoryInfo>(deviceHistory),
            new IonArray<RedeemedCouponInfo>(redemptions),
            new IonArray<UserSpaceInfo>(spaces),
            level,
            stats,
            new IonArray<UserTeamInfo>(teamMemberships),
            head.Friends,
            autoDeleteSettings,
            new IonArray<AdminUserBotInfo>(userBots),
            premiumInfo,
            user.BotEntityId is not null,
            (UserFlag)(int)UserEntity.GetFlags(user, head.BotVerified)
        );
    }

    public async Task<bool> UserExistsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Users.AnyAsync(u => u.Id == userId, ct);
    }

    public async Task<UserActionResult> SetLockdownAsync(Guid userId, LockdownReason reason, DateTimeOffset? expiration,
        bool isAppealable, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new UserActionResult(false, "User not found");

        user.LockdownReason       = reason;
        user.LockDownExpiration   = expiration;
        user.LockDownIsAppealable = isAppealable;

        await db.SaveChangesAsync(ct);

        // The request pipeline checks lockdown against a cached copy; dropping it here, beside the
        // write, is what makes the block take effect on the next call rather than at expiry.
        await cache.RemoveAsync(ArgonRequestContext.LockdownCacheKey(userId), ct);

        return new UserActionResult(true, null);
    }

    public async Task<UserActionResult> ChangeUsernameAsync(Guid userId, string newUsername, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new UserActionResult(false, "User not found");

        var normalizedNew = newUsername.ToLowerInvariant();
        var taken         = await db.Users.AnyAsync(u => u.NormalizedUsername == normalizedNew && u.Id != userId, ct);
        if (taken)
            return new UserActionResult(false, "Username already taken");

        user.Username = newUsername;
        await db.SaveChangesAsync(ct);

        return new UserActionResult(true, null);
    }

    public async Task<UserActionResult> ChangeEmailAsync(Guid userId, string newEmail, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new UserActionResult(false, "User not found");

        var normalizedNew = newEmail.ToLowerInvariant();
        var taken         = await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedNew && u.Id != userId, ct);
        if (taken)
            return new UserActionResult(false, "Email already taken");

        user.Email = newEmail;
        await db.SaveChangesAsync(ct);

        return new UserActionResult(true, null);
    }

    public Task<UserActionResult> RemoveTwoFactorAsync(Guid userId, CancellationToken ct = default)
        => UpdateUserAsync(userId, user => user.TotpSecret = null, ct);

    public Task<UserActionResult> RemovePhoneNumberAsync(Guid userId, CancellationToken ct = default)
        => UpdateUserAsync(userId, user => user.PhoneNumber = null, ct);

    public Task<UserActionResult> ChangeAuthModeAsync(Guid userId, ArgonAuthMode authMode, CancellationToken ct = default)
        => UpdateUserAsync(userId, user => user.PreferredAuthMode = authMode, ct);

    public Task<UserActionResult> ChangeOtpMethodAsync(Guid userId, OtpMethod otpMethod, CancellationToken ct = default)
        => UpdateUserAsync(userId, user => user.PreferredOtpMethod = otpMethod, ct);

    /// <summary>The account-field edits that need nothing but the row: load it, change it, save it.</summary>
    private async Task<UserActionResult> UpdateUserAsync(Guid userId, Action<UserEntity> apply, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new UserActionResult(false, "User not found");

        apply(user);
        await db.SaveChangesAsync(ct);

        return new UserActionResult(true, null);
    }

    // ── devices ──────────────────────────────────────────────────────────────────────────────

    public async Task<DeviceList> GetUserDevicesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        var devices = await db.DeviceObservations
           .AsNoTracking()
           .Where(o => o.UserId == userId)
           .Join(db.DeviceKeys, o => o.DeviceId, k => k.DeviceId, (o, k) => k)
           .OrderByDescending(k => k.LastProvenAt)
           .Select(k => new
            {
                k.DeviceId,
                k.Platform,
                k.Assurance,
                k.ClientName,
                k.EnrolledAt,
                k.LastProvenAt,
                Accounts = db.DeviceObservations.Count(o => o.DeviceId == k.DeviceId),
                IsBanned = db.DeviceBans.Any(b => b.DeviceId == k.DeviceId && (b.ExpiresAt == null || b.ExpiresAt > now))
            })
           .ToListAsync(ct);

        return new DeviceList(devices
           .Select(d => new DeviceSummary(d.DeviceId, (int)d.Platform, (int)d.Assurance, d.ClientName, d.EnrolledAt,
                d.LastProvenAt, d.Accounts, d.IsBanned))
           .ToList());
    }

    /// <summary>
    /// Everyone who has signed in from one machine.
    /// </summary>
    /// <remarks>
    /// The evidence a device ban is decided on, and the collateral it will cause. A shared family
    /// computer and an alt farm produce the same list, so this exists to be read by a person before
    /// <see cref="BanDeviceAsync"/> is used rather than to be counted by anything automatic.
    /// </remarks>
    public async Task<DeviceAccountList> GetDeviceAccountsAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var accounts = await db.DeviceObservations
           .Where(o => o.DeviceId == deviceId)
           .Join(db.Users, o => o.UserId, u => u.Id, (o, u) => new { o, u })
           .OrderByDescending(x => x.o.LastSeenAt)
           .Select(x => new DeviceAccount(
                x.u.Id,
                x.u.Username,
                x.u.DisplayName,
                x.o.FirstSeenAt,
                x.o.LastSeenAt,
                x.o.Logins,
                x.u.LockdownReason != LockdownReason.NONE))
           .ToListAsync(ct);

        return new DeviceAccountList(accounts);
    }

    public async Task<UserActionResult> BanDeviceAsync(Guid deviceId, string reason, DateTimeOffset? expiration,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Only an enrolled machine can be barred: a device the server cannot recognise on sight
        // would shed the ban by clearing a cookie, and a ban that does nothing is worse than
        // none, because it looks like something was done.
        if (!await db.DeviceKeys.AnyAsync(k => k.DeviceId == deviceId, ct))
            return new UserActionResult(false, "Device not found");

        var existing = await db.DeviceBans
           .IgnoreQueryFilters()
           .FirstOrDefaultAsync(b => b.DeviceId == deviceId, ct);

        if (existing is null)
            db.DeviceBans.Add(new DeviceBanEntity
            {
                Id        = ArgonId.New(),
                DeviceId  = deviceId,
                Reason    = reason,
                ExpiresAt = expiration
            });
        else
        {
            // Revived rather than inserted alongside: soft delete leaves the old row holding the
            // unique index, so a second insert would collide with something nobody can see.
            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.Reason    = reason;
            existing.ExpiresAt = expiration;
        }

        await db.SaveChangesAsync(ct);

        return new UserActionResult(true, null);
    }

    public async Task<UserActionResult> UnbanDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var ban = await db.DeviceBans.FirstOrDefaultAsync(b => b.DeviceId == deviceId, ct);

        if (ban is null)
            return new UserActionResult(false, "Device is not banned");

        db.DeviceBans.Remove(ban);

        await db.SaveChangesAsync(ct);

        return new UserActionResult(true, null);
    }

    // ── payments ─────────────────────────────────────────────────────────────────────────────

    public async Task<AdminTransactionPage> GetUserTransactionsAsync(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        page     = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var totalCount = await db.PaymentTransactions.CountAsync(t => t.UserId == userId, ct);

        var username = await db.Users
           .Where(u => u.Id == userId)
           .Select(u => u.Username)
           .FirstOrDefaultAsync(ct) ?? "Unknown";

        // DateTimeOffset.UtcDateTime has no SQL translation. Projecting it inside the query made EF
        // throw while compiling the shaper, so this endpoint failed for every caller regardless of
        // whether the user had any transactions. Materialise first, convert after.
        var rows = await db.PaymentTransactions
           .AsNoTracking()
           .Where(t => t.UserId == userId)
           .OrderByDescending(t => t.CreatedAt)
           .Skip(page * pageSize)
           .Take(pageSize)
           .ToListAsync(ct);

        var transactions = rows
           .Select(t => new AdminTransactionInfo(
                t.Id,
                t.UserId,
                username,
                t.XsollaTxId,
                t.TransactionType,
                t.PlanExternalId,
                t.BoostPackType,
                t.BoostCount,
                t.Amount,
                t.Currency,
                t.RecipientId,
                t.CardSuffix,
                t.CardBrand,
                t.Status,
                t.CreatedAt.UtcDateTime
            ))
           .ToList();

        return new AdminTransactionPage(
            new IonArray<AdminTransactionInfo>(transactions),
            totalCount,
            page,
            pageSize
        );
    }

    public async Task<AdminTransactionDetails?> GetTransactionByXsollaIdAsync(string xsollaTxId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tx = await db.PaymentTransactions
           .AsNoTracking()
           .Include(t => t.User)
           .FirstOrDefaultAsync(t => t.XsollaTxId == xsollaTxId, ct);

        if (tx is null)
            return null;

        var transactionInfo = new AdminTransactionInfo(
            tx.Id,
            tx.UserId,
            tx.User.Username,
            tx.XsollaTxId,
            tx.TransactionType,
            tx.PlanExternalId,
            tx.BoostPackType,
            tx.BoostCount,
            tx.Amount,
            tx.Currency,
            tx.RecipientId,
            tx.CardSuffix,
            tx.CardBrand,
            tx.Status,
            tx.CreatedAt.UtcDateTime
        );

        // Related items granted around the same time
        var relatedItems = await db.Items
           .Where(i => i.OwnerId == tx.UserId && !i.IsReference &&
                       i.CreatedAt >= tx.CreatedAt.AddMinutes(-1) &&
                       i.CreatedAt <= tx.CreatedAt.AddMinutes(5))
           .Select(i => new AdminTransactionItemInfo(i.Id, i.TemplateId, i.CreatedAt.UtcDateTime))
           .ToListAsync(ct);

        var premiumInfo = await ReadPremiumAsync(db, tx.UserId, ct);

        return new AdminTransactionDetails(
            transactionInfo,
            new IonArray<AdminTransactionItemInfo>(relatedItems),
            premiumInfo
        );
    }

    /// <summary>The account's latest subscription as the console shows it, or null when it never had one.</summary>
    private static async Task<AdminPremiumInfo?> ReadPremiumAsync(ApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        var row = await db.UltimaSubscriptions
           .AsNoTracking()
           .Where(s => s.UserId == userId)
           .OrderByDescending(s => s.StartsAt)
           .Select(s => new
            {
                Subscription   = s,
                UsedBoostSlots = db.SpaceBoosts.Count(b => b.SubscriptionId == s.Id && b.SpaceId != null)
            })
           .FirstOrDefaultAsync(ct);

        if (row is null)
            return null;

        var (subscription, usedBoostSlots) = (row.Subscription, row.UsedBoostSlots);

        return new AdminPremiumInfo(
            subscription.Id,
            (UltimaPlan)(int)subscription.Tier,
            (UltimaSubscriptionStatus)(int)subscription.Status,
            subscription.StartsAt.UtcDateTime,
            subscription.ExpiresAt.UtcDateTime,
            subscription.AutoRenew,
            subscription.BoostSlots,
            usedBoostSlots,
            subscription.CancelledAt?.UtcDateTime,
            subscription.XsollaSubscriptionId,
            subscription.ActivatedFromItemId
        );
    }

    // ── trust and deletion ───────────────────────────────────────────────────────────────────

    public async Task<AdminTrustFacts> GetTrustFactsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var user = await db.Users
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new { u.Username, u.CreatedAt, u.UpdatedAt })
           .FirstOrDefaultAsync(ct);

        var trust = await db.UserTrustScores
           .AsNoTracking()
           .FirstOrDefaultAsync(t => t.UserId == userId, ct);

        return new AdminTrustFacts(user?.Username, user?.CreatedAt, user?.UpdatedAt, trust?.AutoActionsApplied ?? 0);
    }

    public async Task<Dictionary<Guid, AdminAccountIdentity>> GetAccountIdentitiesAsync(List<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Users
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(u => ids.Contains(u.Id))
           .Select(u => new AdminAccountIdentity(u.Id, u.Username, u.DisplayName, u.Email))
           .ToDictionaryAsync(u => u.Id, ct);
    }

    /// <summary>What erasing one account would actually destroy.</summary>
    /// <remarks>
    /// <para>Read on demand rather than folded into the queue page: every count below is a query, and
    /// an operator wants them for the one account they are deciding about. The queue lists who and how
    /// long they have been quiet; this answers "and what goes with them", which is the question that
    /// decides whether an approval is routine or needs a conversation first.</para>
    ///
    /// <para>The dates are the sweep's own arithmetic spelled out — the later of the last login and the
    /// last message, the threshold that account is measured against, and whether it chose that
    /// threshold itself — so an operator can see why the account was proposed rather than trusting that
    /// it was. <see cref="AccountDeletionImpact.blockedBy"/> is the bar that would refuse a deletion
    /// asked for right now, which is not always the same as the entry being approvable: a queue entry
    /// is written by a pass that ran up to a day ago.</para>
    /// </remarks>
    public async Task<AccountDeletionImpact> GetAccountDeletionImpactAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync(ct);

        var account = await ctx.Users
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new
            {
                u.Id,
                u.Username,
                u.DisplayName,
                u.Email,
                u.CreatedAt,
                u.HasActiveUltima,
                u.LockdownReason,
                u.LockDownExpiration,
                IsBot            = ctx.BotEntities.Any(bot => bot.BotAsUserId == u.Id),
                AutoDeleteOn     = ctx.AutoDeleteSettings.Where(x => x.UserId == u.Id).Select(x => (bool?)x.Enabled).FirstOrDefault(),
                AutoDeleteMonths = ctx.AutoDeleteSettings.Where(x => x.UserId == u.Id).Select(x => x.Months).FirstOrDefault(),
                LastLogin        = ctx.DeviceHistories.Where(d => d.UserId == u.Id).Max(d => (DateTimeOffset?)d.LastLoginTime),
                LastMessage      = ctx.Messages.Where(m => m.CreatorId == u.Id).Max(m => (DateTimeOffset?)m.CreatedAt),
                Memberships      = ctx.UsersToServerRelations.Count(m => m.UserId == u.Id && !m.IsDeleted),
                Messages         = ctx.Messages.Count(m => m.CreatorId == u.Id && !m.IsDeleted),
                Files            = ctx.Files.Count(f => f.OwnerId == u.Id && !f.IsDeleted),
                Conversations    = ctx.UserConversations.Count(c => c.UserId == u.Id),
                BotsOwned        = ctx.BotEntities.Count(b => !b.IsDeleted
                                    && ctx.TeamEntities.Any(t => t.TeamId == b.TeamId && t.OwnerId == u.Id))
            })
           .FirstOrDefaultAsync(ct);

        if (account is null)
            return Empty(userId);

        // Named rather than counted: "three spaces will be deleted" is a number an operator has to take
        // on trust, and the names are what let them recognise the one that should not have been there.
        var owned = await ctx.Spaces
           .AsNoTracking()
           .Where(space => space.CreatorId == userId && !space.IsDeleted)
           .Select(space => new { space.Name, space.IsCommunity })
           .ToListAsync(ct);

        var deleted     = owned.Where(space => !space.IsCommunity).Select(space => space.Name).ToArray();
        var communities = owned.Where(space => space.IsCommunity).Select(space => space.Name).ToArray();

        var status = await GrainFactory.GetGrain<IAccountDeletionGrain>(userId).GetDeletionStatusAsync();

        var chosen          = account.AutoDeleteOn is true && account.AutoDeleteMonths is > 0;
        var thresholdMonths = chosen
            ? account.AutoDeleteMonths!.Value
            : deletionOptions.Value.DefaultInactivityMonths;

        var lastActivity = (account.LastLogin, account.LastMessage) switch
        {
            ({ } login, { } message) => login > message ? login : message,
            ({ } login, null)        => login,
            (null, { } message)      => message,
            _                        => (DateTimeOffset?)null
        };

        var locked = account.LockdownReason != LockdownReason.NONE
                  && (account.LockDownExpiration is not { } expiry || expiry > DateTimeOffset.UtcNow);

        // The order the grain checks them in, so the answer is the one it would actually give.
        var blockedBy = account.IsBot || userId == UserEntity.SystemUser ? "bot or platform account"
            : locked                                                    ? "standing lockdown"
            : account.HasActiveUltima                                   ? "active subscription"
            : communities.Length > 0                                    ? "owns a community"
            : status.Status is not AccountDeletionStatusKind.None        ? $"deletion already {status.Status}"
            : null;

        return new AccountDeletionImpact(
            userId, true, account.Username, account.DisplayName, account.Email,
            account.CreatedAt.UtcDateTime,
            lastActivity?.UtcDateTime,
            account.LastLogin?.UtcDateTime,
            account.LastMessage?.UtcDateTime,
            thresholdMonths,
            chosen,
            account.HasActiveUltima,
            locked,
            account.IsBot,
            deleted.Length, new IonArray<string>(deleted),
            communities.Length, new IonArray<string>(communities),
            account.Memberships,
            account.Messages,
            account.Files,
            account.Conversations,
            account.BotsOwned,
            status.Status switch
            {
                AccountDeletionStatusKind.Scheduled => AccountDeletionStatusView.SCHEDULED,
                AccountDeletionStatusKind.Executing => AccountDeletionStatusView.EXECUTING,
                AccountDeletionStatusKind.Completed => AccountDeletionStatusView.COMPLETED,
                AccountDeletionStatusKind.Failed    => AccountDeletionStatusView.FAILED,
                _                                   => AccountDeletionStatusView.NONE
            },
            status.ScheduledAt?.UtcDateTime,
            status.ExecutionAt?.UtcDateTime,
            blockedBy);

        static AccountDeletionImpact Empty(Guid id)
            => new(id, false, "", "", "", default, null, null, null, 0, false, false, false, false,
                0, new IonArray<string>([]), 0, new IonArray<string>([]), 0, 0, 0, 0, 0,
                AccountDeletionStatusView.NONE, null, null, "no such account");
    }
}
