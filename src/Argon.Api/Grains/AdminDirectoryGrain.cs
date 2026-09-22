namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Grains.Interfaces;
using ConsoleContracts;
using ion.runtime;
using Orleans.Concurrency;

/// <inheritdoc cref="IAdminDirectoryGrain"/>
[StatelessWorker]
public sealed class AdminDirectoryGrain(IDbContextFactory<ApplicationDbContext> dbFactory) : Grain, IAdminDirectoryGrain
{
    // ── spaces ───────────────────────────────────────────────────────────────────────────────

    public async Task<AdminSpaceSearchResult> SearchSpaceAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AdminSpaceSearchResult(false, null, SpaceSearchMatchKind.None);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Try GUID first
        if (Guid.TryParse(query, out var spaceId))
        {
            var space = await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId && !s.IsDeleted, ct);
            if (space is not null)
            {
                var memberCount  = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == spaceId && !x.IsDeleted, ct);
                var channelCount = await db.Channels.CountAsync(c => c.SpaceId == spaceId && !c.IsDeleted, ct);
                return new AdminSpaceSearchResult(true,
                    new AdminSpaceSummary(space.Id, space.Name, space.AvatarFileId, memberCount, channelCount, space.CreatedAt.UtcDateTime),
                    SpaceSearchMatchKind.SpaceId);
            }
        }

        // Search by name
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var byName = await db.Spaces
           .FirstOrDefaultAsync(s => !s.IsDeleted && s.NormalizedName == normalizedQuery, ct);

        if (byName is not null)
        {
            var memberCount  = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == byName.Id && !x.IsDeleted, ct);
            var channelCount = await db.Channels.CountAsync(c => c.SpaceId == byName.Id && !c.IsDeleted, ct);
            return new AdminSpaceSearchResult(true,
                new AdminSpaceSummary(byName.Id, byName.Name, byName.AvatarFileId, memberCount, channelCount, byName.CreatedAt.UtcDateTime),
                SpaceSearchMatchKind.Name);
        }

        return new AdminSpaceSearchResult(false, null, SpaceSearchMatchKind.None);
    }

    public async Task<AdminSpaceCard?> GetSpaceCardAsync(Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var space = await db.Spaces
           .AsNoTracking()
           .Include(s => s.Channels.Where(c => !c.IsDeleted))
           .Include(s => s.ChannelGroups.Where(g => !g.IsDeleted))
           .Include(s => s.Archetypes.Where(a => !a.IsDeleted))
           .FirstOrDefaultAsync(s => s.Id == spaceId && !s.IsDeleted, ct);

        if (space is null)
            return null;

        var creator = await db.Users
           .Where(u => u.Id == space.CreatorId)
           .Select(u => new AdminUserSummary(u.Id, u.Username, u.DisplayName, u.AvatarFileId))
           .FirstOrDefaultAsync(ct) ?? new AdminUserSummary(space.CreatorId, "Unknown", "Unknown", null);

        var memberCount = await db.UsersToServerRelations.CountAsync(x => x.SpaceId == spaceId && !x.IsDeleted, ct);

        // The channel's last-message id is no longer on the channel row — ChannelEntity.LastMessageId
        // is a column nothing writes any more — so it comes from ChannelLastMessages, one seek by
        // space. The Redis cell would be up to a flush interval fresher and is not consulted: an
        // operator looking at a space card is not reading unread state, and a support answer that
        // disagrees with the database by three seconds is worse than one that is the database.
        // A channel with no row has had nothing posted in it, and shows zero, which is what the
        // column said for such a channel too.
        var storedMarks = await db.ChannelLastMessages
           .AsNoTracking()
           .Where(m => m.SpaceId == spaceId)
           .Select(m => new { m.ChannelId, m.LastMessageId })
           .ToDictionaryAsync(m => m.ChannelId, m => m.LastMessageId, ct);

        var channels = space.Channels.Select(c => new AdminChannelInfo(
            c.Id,
            c.Name,
            (ChannelType)(int)c.ChannelType,
            c.Description,
            c.ChannelGroupId,
            c.SlowMode.HasValue ? (int)c.SlowMode.Value.TotalSeconds : null,
            c.DoNotRestrictBoosters,
            storedMarks.GetValueOrDefault(c.Id)
        )).ToList();

        var channelGroups = space.ChannelGroups.Select(g => new AdminChannelGroupInfo(
            g.Id,
            g.Name,
            g.Description,
            space.Channels.Count(c => c.ChannelGroupId == g.Id)
        )).ToList();

        // Archetype member counts
        var archetypeMemberCounts = await db.MemberArchetypes
           .Where(ma => space.Archetypes.Select(a => a.Id).Contains(ma.ArchetypeId))
           .GroupBy(ma => ma.ArchetypeId)
           .Select(g => new { g.Key, Count = g.Count() })
           .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var archetypes = space.Archetypes.Select(a => new AdminArchetypeInfo(
            a.Id,
            a.Name,
            (ArgonEntitlement)(ulong)a.Entitlement,
            a.IsDefault,
            a.IsLocked,
            a.IsHidden,
            archetypeMemberCounts.GetValueOrDefault(a.Id, 0)
        )).ToList();

        // Installed bots, each with the locked archetype its install created
        var botMembers = await db.UsersToServerRelations
           .AsNoTracking()
           .Where(sm => sm.SpaceId == spaceId && !sm.IsDeleted)
           .Join(db.Users.Where(u => u.BotEntityId != null),
                sm => sm.UserId, u => u.Id, (sm, u) => new { sm, u })
           .Join(db.BotEntities,
                x => x.u.BotEntityId, b => b.AppId, (x, b) => new { x.sm, x.u, b })
           .Select(x => new
            {
                x.b.AppId,
                x.b.Name,
                x.u.Username,
                x.u.AvatarFileId,
                x.b.IsVerified,
                x.b.RequiredEntitlements,
                Granted = db.Archetypes
                   .Where(a => a.SpaceId == spaceId && a.IsLocked && !a.IsDeleted)
                   .Join(db.MemberArchetypes.Where(ma => ma.SpaceMemberId == x.sm.Id),
                        a => a.Id, ma => ma.ArchetypeId, (a, _) => (ArgonEntitlement?)a.Entitlement)
                   .FirstOrDefault()
            })
           .ToListAsync(ct);

        var installedBots = botMembers.Select(bm => new AdminSpaceBotInfo(
            bm.AppId,
            bm.Name,
            bm.Username,
            bm.AvatarFileId,
            bm.IsVerified,
            (ArgonEntitlement)(ulong)(bm.Granted ?? ArgonEntitlement.None),
            bm.Granted is { } granted && (ulong)granted != (ulong)bm.RequiredEntitlements
        )).ToList();

        // Recent invites (last 10)
        var invites = await db.Invites
           .AsNoTracking()
           .Where(i => i.SpaceId == spaceId)
           .OrderByDescending(i => i.CreatedAt)
           .Take(10)
           .Select(i => new
            {
                i.Id,
                i.CreatorId,
                i.ExpireAt,
                Issuer = db.Users.Where(u => u.Id == i.CreatorId).Select(u => u.Username).FirstOrDefault()
            })
           .ToListAsync(ct);

        var recentInvites = invites.Select(i => new AdminInviteInfo(
            i.Id.ToString(),
            i.CreatorId,
            i.Issuer ?? "Unknown",
            i.ExpireAt.UtcDateTime,
            0
        )).ToList();

        return new AdminSpaceCard(
            space.Id,
            space.Name,
            space.Description,
            space.AvatarFileId,
            space.TopBannedFileId,
            space.IsCommunity,
            space.BoostCount,
            space.BoostLevel,
            creator,
            space.CreatedAt.UtcDateTime,
            memberCount,
            space.Channels.Count,
            botMembers.Count,
            new IonArray<AdminChannelInfo>(channels),
            new IonArray<AdminChannelGroupInfo>(channelGroups),
            new IonArray<AdminArchetypeInfo>(archetypes),
            new IonArray<AdminSpaceBotInfo>(installedBots),
            new IonArray<AdminInviteInfo>(recentInvites),
            space.IsVerified,
            space.IsOfficial
        );
    }

    public async Task<bool> SpaceExistsAsync(Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Spaces.AnyAsync(s => s.Id == spaceId && !s.IsDeleted, ct);
    }

    public async Task<AdminSpaceMemberPage> GetSpaceMembersAsync(Guid spaceId, int offset, int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        offset = Math.Max(0, offset);
        limit  = Math.Clamp(limit, 1, 100);

        var totalCount = await db.UsersToServerRelations
           .CountAsync(sm => sm.SpaceId == spaceId && !sm.IsDeleted, ct);

        var memberEntities = await db.UsersToServerRelations
           .AsNoTracking()
           .Where(sm => sm.SpaceId == spaceId && !sm.IsDeleted)
           .OrderBy(sm => sm.CreatedAt)
           .Skip(offset)
           .Take(limit)
           .Include(sm => sm.User)
           .Include(sm => sm.SpaceMemberArchetypes).ThenInclude(sma => sma.Archetype)
           .ToListAsync(ct);

        var members = memberEntities.Select(sm => new AdminSpaceMemberInfo(
            sm.UserId,
            sm.User.Username,
            sm.User.DisplayName,
            sm.User.AvatarFileId,
            sm.CreatedAt.UtcDateTime,
            new IonArray<string>(sm.SpaceMemberArchetypes.Select(sma => sma.Archetype.Name).ToList())
        )).ToList();

        return new AdminSpaceMemberPage(
            new IonArray<AdminSpaceMemberInfo>(members),
            totalCount,
            offset,
            limit
        );
    }

    // ── bots ─────────────────────────────────────────────────────────────────────────────────

    public async Task<AdminBotSearchResult> SearchBotAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AdminBotSearchResult(false, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalizedQuery = query.Trim().ToLowerInvariant();

        // Try GUID first
        if (Guid.TryParse(query, out var appId))
        {
            var bot = await db.BotEntities
               .Include(b => b.BotAsUser)
               .Include(b => b.Team)
               .FirstOrDefaultAsync(b => b.AppId == appId, ct);

            if (bot is not null)
                return new AdminBotSearchResult(true, MapBotSummary(bot));
        }

        // Username, then name: two indexed lookups rather than one OR across Users and DevApps.
        var byName = await FindBotAsync(b => b.BotAsUser.NormalizedUsername == normalizedQuery)
                     ?? await FindBotAsync(b => b.NormalizedName == normalizedQuery);

        return byName is not null
            ? new AdminBotSearchResult(true, MapBotSummary(byName))
            : new AdminBotSearchResult(false, null);

        Task<BotEntity?> FindBotAsync(System.Linq.Expressions.Expression<Func<BotEntity, bool>> predicate)
            => db.BotEntities
               .Include(b => b.BotAsUser)
               .Include(b => b.Team)
               .FirstOrDefaultAsync(predicate, ct);
    }

    public async Task<AdminBotCard?> GetBotCardAsync(Guid appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var bot = await db.BotEntities
           .AsNoTracking()
           .Include(b => b.BotAsUser)
           .Include(b => b.Team).ThenInclude(t => t.Owner)
           .FirstOrDefaultAsync(b => b.AppId == appId, ct);

        if (bot is null)
            return null;

        var memberships = db.UsersToServerRelations.Where(sm => sm.UserId == bot.BotAsUserId && !sm.IsDeleted);

        var installedCount = await memberships.CountAsync(ct);

        // A bot can sit in thousands of spaces, and every row here costs a member count, so the card
        // lists the latest installs; the total above stays exact.
        const int shown = 100;

        var installedRows = await memberships
           .AsNoTracking()
           .OrderByDescending(sm => sm.CreatedAt)
           .Take(shown)
           .Select(sm => new
            {
                sm.SpaceId,
                sm.Space.Name,
                sm.Space.AvatarFileId,
                Members = db.UsersToServerRelations.Count(x => x.SpaceId == sm.SpaceId && !x.IsDeleted),
                Granted = db.Archetypes
                   .Where(a => a.SpaceId == sm.SpaceId && a.IsLocked && !a.IsDeleted)
                   .Join(db.MemberArchetypes.Where(ma => ma.SpaceMemberId == sm.Id),
                        a => a.Id, ma => ma.ArchetypeId, (a, _) => (ArgonEntitlement?)a.Entitlement)
                   .FirstOrDefault()
            })
           .ToListAsync(ct);

        var installedSpaces = installedRows.Select(sm => new AdminBotSpaceInfo(
            sm.SpaceId,
            sm.Name,
            sm.AvatarFileId,
            sm.Members,
            (ArgonEntitlement)(ulong)(sm.Granted ?? ArgonEntitlement.None),
            sm.Granted is { } granted && (ulong)granted != (ulong)bot.RequiredEntitlements
        )).ToList();

        // Commands
        var commands = await db.BotCommands
           .Where(c => c.AppId == appId)
           .Select(c => new AdminBotCommandInfo(
                c.CommandId,
                c.Name,
                c.Description,
                c.Options != null ? c.Options.Count : 0,
                c.SpaceId == null
            ))
           .ToListAsync(ct);

        var team = MapTeamSummary(bot.Team);
        var creator = new AdminUserSummary(
            bot.Team.Owner.Id,
            bot.Team.Owner.Username,
            bot.Team.Owner.DisplayName,
            bot.Team.Owner.AvatarFileId
        );

        return new AdminBotCard(
            bot.AppId,
            bot.Name,
            bot.BotAsUser.Username,
            bot.Description,
            bot.BotAsUser.AvatarFileId,
            bot.IsVerified,
            bot.IsPublic,
            bot.IsInternalApp,
            (AdminBotLifecycleState)(int)bot.LifecycleState,
            bot.MaxSpaces,
            installedCount,
            (ArgonEntitlement)(ulong)bot.RequiredEntitlements,
            new IonArray<string>(bot.RequiredScopes),
            bot.CreatedAt.UtcDateTime,
            team,
            creator,
            new IonArray<AdminBotSpaceInfo>(installedSpaces),
            new IonArray<AdminBotCommandInfo>(commands)
        );
    }

    public Task<UserActionResult> SetBotVerifiedAsync(Guid appId, bool isVerified, CancellationToken ct = default)
        => UpdateBotAsync(appId, bot => bot.IsVerified = isVerified, ct);

    public Task<UserActionResult> SetBotMaxSpacesAsync(Guid appId, int maxSpaces, CancellationToken ct = default)
        => UpdateBotAsync(appId, bot => bot.MaxSpaces = maxSpaces, ct);

    public Task<UserActionResult> SetBotLifecycleStateAsync(Guid appId, AdminBotLifecycleState state, CancellationToken ct = default)
        => UpdateBotAsync(appId, bot => bot.LifecycleState = (BotLifecycleState)(int)state, ct);

    public async Task<UserActionResult> SetAppInternalAsync(Guid appId, bool isInternalApp, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var app = await db.AppEntities.FirstOrDefaultAsync(a => a.AppId == appId, ct);
        if (app is null) return new UserActionResult(false, "App not found");

        app.IsInternalApp = isInternalApp;
        await db.SaveChangesAsync(ct);

        return new UserActionResult(true, null);
    }

    /// <summary>The bot-flag buttons, which differ only in the field they set.</summary>
    private async Task<UserActionResult> UpdateBotAsync(Guid appId, Action<BotEntity> apply, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var bot = await db.BotEntities.FirstOrDefaultAsync(b => b.AppId == appId, ct);
        if (bot is null) return new UserActionResult(false, "Bot not found");

        apply(bot);
        await db.SaveChangesAsync(ct);

        return new UserActionResult(true, null);
    }

    // ── teams and internal apps ──────────────────────────────────────────────────────────────

    public async Task<AdminTeamSearchResult> SearchTeamAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AdminTeamSearchResult(false, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Try GUID first
        if (Guid.TryParse(query, out var teamId))
        {
            var team = await db.TeamEntities
               .Include(t => t.Members)
               .Include(t => t.Applications)
               .FirstOrDefaultAsync(t => t.TeamId == teamId && !t.IsDeleted, ct);

            if (team is not null)
                return new AdminTeamSearchResult(true, MapTeamSummary(team));
        }

        // Search by name
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var byName = await db.TeamEntities
           .Include(t => t.Members)
           .Include(t => t.Applications)
           .FirstOrDefaultAsync(t => !t.IsDeleted && t.NormalizedName == normalizedQuery, ct);

        return byName is not null
            ? new AdminTeamSearchResult(true, MapTeamSummary(byName))
            : new AdminTeamSearchResult(false, null);
    }

    public async Task<AdminTeamCard?> GetTeamCardAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var team = await db.TeamEntities
           .AsNoTracking()
           .Include(t => t.Owner)
           .Include(t => t.Members).ThenInclude(m => m.User)
           .Include(t => t.Applications)
           .FirstOrDefaultAsync(t => t.TeamId == teamId && !t.IsDeleted, ct);

        if (team is null)
            return null;

        var owner = new AdminUserSummary(
            team.Owner.Id,
            team.Owner.Username,
            team.Owner.DisplayName,
            team.Owner.AvatarFileId
        );

        var members = team.Members.Select(m => new AdminTeamMemberInfo(
            m.UserId,
            m.User.Username,
            m.User.DisplayName,
            m.User.AvatarFileId,
            m.IsOwner,
            m.JoinedAt,
            new IonArray<string>(m.Claims ?? [])
        )).ToList();

        var apps = team.Applications.Select(a =>
        {
            var isVerified = a is BotEntity bot ? bot.IsVerified : a is ClientAppEntity client && client.IsVerified;
            return new AdminTeamAppInfo(
                a.AppId,
                a.Name,
                (AdminDevAppType)(int)a.AppType,
                a.IsInternalApp,
                isVerified,
                a.CreatedAt.UtcDateTime
            );
        }).ToList();

        return new AdminTeamCard(
            team.TeamId,
            team.Name,
            team.AvatarFileId,
            owner,
            team.CreatedAt.UtcDateTime,
            new IonArray<AdminTeamMemberInfo>(members),
            new IonArray<AdminTeamAppInfo>(apps)
        );
    }

    public async Task<InternalAppSearchResult> SearchInternalAppsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new InternalAppSearchResult(new IonArray<InternalAppInfo>([]));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalizedQuery = query.Trim().ToLowerInvariant();

        // Try GUID first
        if (Guid.TryParse(query, out var appId))
        {
            var byId = await db.AppEntities
               .AsNoTracking()
               .Include(a => a.Team)
               .Where(a => a.AppId == appId && a.IsInternalApp && !a.IsDeleted)
               .FirstOrDefaultAsync(ct);

            if (byId is not null)
                return new InternalAppSearchResult(new IonArray<InternalAppInfo>([MapInternalApp(byId)]));
        }

        // Substring match, so a scan of DevApps; the table holds only developer-registered apps.
        var byNameOrClient = await db.AppEntities
           .AsNoTracking()
           .Include(a => a.Team)
           .Where(a => a.IsInternalApp && !a.IsDeleted &&
                       (a.NormalizedName.Contains(normalizedQuery) ||
                        a.ClientId.ToLower().Contains(normalizedQuery)))
           .Take(20)
           .ToListAsync(ct);

        // Also search by bot username
        var byBotUsername = await db.BotEntities
           .AsNoTracking()
           .Include(b => b.BotAsUser)
           .Include(b => b.Team)
           .Where(b => b.IsInternalApp && !b.IsDeleted &&
                       b.BotAsUser.NormalizedUsername.Contains(normalizedQuery))
           .Take(20)
           .ToListAsync(ct);

        var results = byNameOrClient
           .Select(MapInternalApp)
           .Concat(byBotUsername.Select(b => MapInternalApp((DevAppEntity)b)))
           .DistinctBy(x => x.appId)
           .ToList();

        return new InternalAppSearchResult(new IonArray<InternalAppInfo>(results));
    }

    private static InternalAppInfo MapInternalApp(DevAppEntity app) => new(
        app.AppId,
        app.Name,
        app.ClientId,
        app.Description,
        (AdminDevAppType)(int)app.AppType,
        app.TeamId,
        app.Team?.Name ?? "",
        app.IsInternalApp
    );

    private static AdminBotSummary MapBotSummary(BotEntity bot) => new(
        bot.AppId,
        bot.Name,
        bot.BotAsUser.Username,
        bot.Description,
        bot.BotAsUser.AvatarFileId,
        bot.IsVerified,
        bot.IsPublic,
        bot.TeamId,
        bot.Team.Name
    );

    private static AdminTeamSummary MapTeamSummary(DevTeamEntity team) => new(
        team.TeamId,
        team.Name,
        team.AvatarFileId,
        team.OwnerId,
        team.Members?.Count ?? 0,
        team.Applications?.Count ?? 0,
        team.CreatedAt.UtcDateTime
    );
}
