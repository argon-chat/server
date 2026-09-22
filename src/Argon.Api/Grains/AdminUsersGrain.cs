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

        var user = await db.Users
           .Include(u => u.Profile)
           .Include(u => u.BotEntity)
           .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        // Get last login from device history
        var lastLogin = await db.DeviceHistories
           .Where(d => d.UserId == userId && d.LastLoginTime != null)
           .OrderByDescending(d => d.LastLoginTime)
           .Select(d => d.LastLoginTime)
           .FirstOrDefaultAsync(ct);

        // Get blocked users count
        var blockedUsersCount = await db.UserBlocklist.CountAsync(b => b.UserId == userId, ct);

        // Get direct messages count (sent by user)
        var directMessagesCount = await db.DirectMessages.CountAsync(m => m.SenderId == userId, ct);

        // Get conversations count (where user is a participant)
        var conversationsCount = await db.Conversations
           .CountAsync(c => c.Participant1Id == userId || c.Participant2Id == userId, ct);

        // Account info
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
            lastLogin?.UtcDateTime,
            blockedUsersCount,
            directMessagesCount,
            conversationsCount
        );

        // Profile info
        var profile = user.Profile.ToDto();

        // Passkeys count & TwoFactor
        var passkeyCount = await db.Passkeys.CountAsync(p => p.UserId == userId && p.IsCompleted, ct);
        var hasTwoFactor = !string.IsNullOrEmpty(user.TotpSecret);

        // Items with box contents
        var itemEntities = await db.Items
           .Where(i => i.OwnerId == userId && !i.IsReference)
           .Include(i => i.Scenario)
           .ToListAsync(ct);

        var items = new List<InventoryItemInfo>();
        foreach (var item in itemEntities)
        {
            var isBox       = item.Scenario is BoxScenario or QualifierBox or MultipleQualifierBox;
            var boxContents = IonArray<BoxContentInfo>.Empty;

            if (isBox && item.Scenario is QualifierBox qb && qb.ReferenceItemId != Guid.Empty)
            {
                var refItem = await db.Items.FirstOrDefaultAsync(i => i.Id == qb.ReferenceItemId, ct);
                if (refItem is not null)
                    boxContents = new IonArray<BoxContentInfo>([new BoxContentInfo(refItem.Id, refItem.TemplateId)]);
            }

            if (isBox && item.Scenario is MultipleQualifierBox { ReferenceItemIds.Count: > 0 } mqb)
            {
                var refItems = await db.Items.Where(i => mqb.ReferenceItemIds.Contains(i.Id)).ToListAsync(ct);
                if (refItems.Count > 0)
                    boxContents = new IonArray<BoxContentInfo>(refItems.Select(ri => new BoxContentInfo(ri.Id, ri.TemplateId)).ToList());
            }

            items.Add(new InventoryItemInfo(
                item.Id,
                item.TemplateId,
                item.IsUsable,
                item.IsGiftable,
                item.ReceivedFrom,
                item.TTL.HasValue ? (int)item.TTL.Value.TotalSeconds : null,
                item.CreatedAt.UtcDateTime,
                isBox,
                boxContents
            ));
        }

        // Recent messages (last 10)
        var messages = await db.Messages
           .Where(m => m.CreatorId == userId)
           .OrderByDescending(m => m.CreatedAt)
           .Take(10)
           .Select(m => new
            {
                m.MessageId,
                m.SpaceId,
                m.ChannelId,
                m.Text,
                m.CreatedAt
            })
           .ToListAsync(ct);

        var spaceIds   = messages.Select(m => m.SpaceId).Distinct().ToList();
        var channelIds = messages.Select(m => m.ChannelId).Distinct().ToList();

        var spaceNames = await db.Spaces
           .Where(s => spaceIds.Contains(s.Id))
           .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var channelNames = await db.Channels
           .Where(c => channelIds.Contains(c.Id))
           .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var recentMessages = messages.Select(m => new MessageInfo(
            m.MessageId,
            m.SpaceId,
            spaceNames.GetValueOrDefault(m.SpaceId, "Unknown"),
            m.ChannelId,
            channelNames.GetValueOrDefault(m.ChannelId, "Unknown"),
            m.Text,
            m.CreatedAt.UtcDateTime
        )).ToArray();

        // Device history (last 10)
        var deviceHistoryEntities = await db.DeviceHistories
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

        // Redeemed coupons
        var redemptionEntities = await db.CouponRedemption
           .Where(r => r.UserId == userId)
           .Include(r => r.Coupon)
           .Include(r => r.Items)
           .ToListAsync(ct);

        var redemptions = redemptionEntities.Select(r => new RedeemedCouponInfo(
            r.CouponId,
            r.Coupon.Code,
            r.RedeemedAt.UtcDateTime,
            r.Items.Count
        )).ToList();

        // Spaces (first 5)
        var spaceMemberships = await db.UsersToServerRelations
           .Where(sm => sm.UserId == userId && !sm.IsDeleted)
           .OrderByDescending(sm => sm.CreatedAt)
           .Take(5)
           .Include(sm => sm.Space)
           .ToListAsync(ct);

        var spaces = new List<UserSpaceInfo>();
        foreach (var sm in spaceMemberships)
        {
            var memberCount  = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == sm.SpaceId && !x.IsDeleted, ct);
            var channelCount = await db.Channels.CountAsync(c => c.SpaceId == sm.SpaceId, ct);
            var isOwner      = sm.Space.CreatorId == userId;

            spaces.Add(new UserSpaceInfo(
                sm.SpaceId,
                sm.Space.Name,
                sm.Space.AvatarFileId,
                memberCount,
                channelCount,
                sm.CreatedAt.UtcDateTime,
                isOwner
            ));
        }

        // Level info
        var levelEntity = await db.UserLevels.FirstOrDefaultAsync(l => l.UserId == userId, ct);
        var level = levelEntity is not null
            ? new UserLevelInfo(
                levelEntity.CurrentLevel,
                levelEntity.CurrentCycleXp,
                levelEntity.TotalXpAllTime,
                levelEntity.CanClaimMedal,
                levelEntity.LastXpAward.UtcDateTime)
            : new UserLevelInfo(1, 0, 0, false, DateTime.UtcNow);

        // Stats (aggregated)
        var statsEntities = await db.UserDailyStats
           .Where(s => s.UserId == userId)
           .ToListAsync(ct);

        var stats = new UserStatsInfo(
            statsEntities.Sum(s => s.TimeInVoiceSeconds),
            statsEntities.Sum(s => s.CallsMade),
            statsEntities.Sum(s => s.MessagesSent),
            statsEntities.Sum(s => s.XpEarned)
        );

        // Teams (first 3)
        var teamMemberships = await db.MemberTeamEntities
           .Where(tm => tm.UserId == userId)
           .Take(3)
           .Include(tm => tm.Team)
           .Select(tm => new UserTeamInfo(
                tm.TeamId,
                tm.Team.Name,
                tm.Team.AvatarFileId,
                tm.IsOwner,
                tm.JoinedAt
            ))
           .ToListAsync(ct);

        // Friend count
        var friendCount = await db.Friends.CountAsync(f => f.UserId == userId, ct);

        // Auto-delete settings
        var autoDeleteEntity = await db.AutoDeleteSettings.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        var autoDeleteSettings = autoDeleteEntity is not null
            ? new AutoDeleteSettingsInfo(autoDeleteEntity.Enabled, autoDeleteEntity.Months)
            : null;

        // Bots owned by user (through team membership)
        var userTeamIds = await db.MemberTeamEntities
           .Where(tm => tm.UserId == userId)
           .Select(tm => tm.TeamId)
           .ToListAsync(ct);

        var userBots = userTeamIds.Count > 0
            ? await db.BotEntities
               .Where(b => userTeamIds.Contains(b.TeamId))
               .Include(b => b.BotAsUser)
               .Include(b => b.Team)
               .Select(b => new AdminUserBotInfo(
                    b.AppId,
                    b.Name,
                    b.BotAsUser.Username,
                    b.IsVerified,
                    b.LifecycleState == BotLifecycleState.Published,
                    b.TeamId,
                    b.Team.Name
                ))
               .ToListAsync(ct)
            : [];

        // Premium info
        var premiumInfo = await ReadPremiumAsync(db, userId, ct);

        // Bot flag & user flags
        var isBot = user.BotEntityId is not null;
        var flags = (UserFlag)(int)UserEntity.GetFlags(user);

        return new UserCardDetails(
            account,
            profile,
            passkeyCount,
            hasTwoFactor,
            new IonArray<InventoryItemInfo>(items),
            new IonArray<MessageInfo>(recentMessages),
            new IonArray<DeviceHistoryInfo>(deviceHistory),
            new IonArray<RedeemedCouponInfo>(redemptions),
            new IonArray<UserSpaceInfo>(spaces),
            level,
            stats,
            new IonArray<UserTeamInfo>(teamMemberships),
            friendCount,
            autoDeleteSettings,
            new IonArray<AdminUserBotInfo>(userBots),
            premiumInfo,
            isBot,
            flags
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
           .Where(o => o.UserId == userId)
           .Join(db.DeviceKeys, o => o.DeviceId, k => k.DeviceId, (o, k) => k)
           .OrderByDescending(k => k.LastProvenAt)
           .ToListAsync(ct);

        var summaries = new List<DeviceSummary>(devices.Count);

        foreach (var key in devices)
        {
            summaries.Add(new DeviceSummary(
                key.DeviceId,
                (int)key.Platform,
                (int)key.Assurance,
                key.ClientName,
                key.EnrolledAt,
                key.LastProvenAt,
                await db.DeviceObservations.CountAsync(o => o.DeviceId == key.DeviceId, ct),
                await db.DeviceBans.AnyAsync(b => b.DeviceId == key.DeviceId && (b.ExpiresAt == null || b.ExpiresAt > now), ct)));
        }

        return new DeviceList(summaries);
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
        var subscription = await db.UltimaSubscriptions
           .Where(s => s.UserId == userId)
           .OrderByDescending(s => s.StartsAt)
           .FirstOrDefaultAsync(ct);

        if (subscription is null)
            return null;

        var usedBoostSlots = await db.SpaceBoosts
           .CountAsync(b => b.SubscriptionId == subscription.Id && b.SpaceId != null, ct);

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
