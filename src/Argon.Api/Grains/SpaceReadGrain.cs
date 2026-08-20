namespace Argon.Grains;

using Argon.Features.Cache;
using Argon.Entities;
using Grains.Interfaces;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;
using Services;
using Services.L1L2;
using StackExchange.Redis;

/// <summary>
/// Queries about a space, answered by a pool of activations over a shared cache rather than by the
/// one <see cref="SpaceGrain"/> activation. See <see cref="ISpaceReadGrain"/> for why they moved.
/// </summary>
[StatelessWorker]
public sealed class SpaceReadGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    IUserPresenceService userPresence,
    IArchetypeAgent archetypes,
    [FromKeyedServices(RedisProfiles.Cache)] IRedisPoolConnections redis,
    HybridCache cache,
    ILogger<SpaceReadGrain> logger) : Grain, ISpaceReadGrain
{
    /// <summary>
    /// Short rather than long. Tag invalidation from the write side is the primary mechanism and this
    /// is only the backstop behind it, but it still has to be long enough that a crowd logging in
    /// together shares one answer instead of each paying for the query.
    /// </summary>
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromSeconds(30)
    };

    public async Task<SpaceSnapshot> GetSnapshot(SpaceVersions? known)
    {
        var spaceId  = this.GetPrimaryKey();
        var callerId = this.GetUserId();

        var roster = await CachedRosterAsync(spaceId);
        var roles  = await CachedArchetypesAsync(spaceId);

        if (MemberOf(roster.Value, roles.Value, callerId) is not { } member)
            throw new InvalidOperationException($"user '{callerId}' is not a member of space '{spaceId}'");

        var channels = await CachedChannelsAsync(spaceId);
        var groups   = await CachedGroupsAsync(spaceId);

        // The caller's own token, not the space's: which channels they may see depends on their roles
        // as much as on the channel rows, and two members of one space can hold different answers.
        var visibleVersion = SpaceReadVersion.Combine(
            channels.Version, member.Id.ToString(), string.Join(',', member.ArchetypeIds.Order()));

        var versions = new SpaceVersions(roster.Version, visibleVersion, groups.Version, roles.Version);

        // Guarded rather than folded into the call below, because this is the branch that costs
        // something: it fans out to every visible channel for its live occupancy. A caller who is up
        // to date makes no grain calls at all.
        //
        // Which means a caller whose token matches is not told anyone posted, because lastMessageId
        // is deliberately not part of the token (see CachedChannel.VersionOf). That is not a hole:
        // unread state reaches the client through GetGlobalBadges, which reads the channel rows
        // straight out of the database, and through the live MessageSent events. What used to happen
        // instead was that every space with traffic re-sent its whole channel list on a two-minute
        // timer to deliver a number the client had two better sources for.
        IonArray<RealtimeChannel>? visible = null;

        if (known?.channels != versions.channels)
            visible = new IonArray<RealtimeChannel>(await VisibleChannelsAsync(channels.Value, member));

        return new SpaceSnapshot(versions,
            Unless(known?.members == versions.members, roster.Value),
            visible,
            Unless(known?.groups == versions.groups, groups.Value),
            Unless(known?.archetypes == versions.archetypes, roles.Value));
    }

    /// <summary>
    /// The part, or nothing at all when the caller's token already matched. Null is not an empty
    /// array here: an empty array says the space genuinely has none of that thing.
    /// </summary>
    private static IonArray<T>? Unless<T>(bool matched, List<T> value)
        => matched ? null : (IonArray<T>?)new IonArray<T>(value);

    /// <summary>
    /// Held for a second, which is the difference between a crowd asking once and asking each.
    /// </summary>
    /// <remarks>
    /// Every arriving client asks for the presence of everyone else, so a hundred and fifty arrivals
    /// were a hundred and fifty batch reads of the same hundred and fifty keys inside the same
    /// fraction of a second. One second of staleness costs nothing here: this is the first paint, and
    /// the event stream corrects it immediately after — which is where the client gets every
    /// subsequent change anyway.
    /// </remarks>
    private static readonly HybridCacheEntryOptions PresenceOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(1),
        LocalCacheExpiration = TimeSpan.FromSeconds(1)
    };

    public async Task<List<MemberPresence>> GetPresence()
    {
        var spaceId = this.GetPrimaryKey();
        var roster  = await CachedRosterAsync(spaceId);

        return await cache.GetOrCreateAsync($"space:presence:{spaceId}",
            (userPresence, roster.Value),
            static async (state, _) =>
            {
                var ids = state.Value.Select(x => x.userId).Distinct().ToList();

                var statuses   = await state.userPresence.BatchGetAggregatedStatusAsync(ids);
                var activities = await state.userPresence.BatchGetUsersActivityPresence(ids);

                return ids.Select(id => new MemberPresence(id,
                    statuses.TryGetValue(id, out var status) ? status : UserStatus.Offline,
                    activities.TryGetValue(id, out var activity) ? activity : null)).ToList();
            },
            PresenceOptions, [ISpaceReadCache.SpaceTag(spaceId)]);
    }

    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        var spaceId = this.GetPrimaryKey();
        var members = await CachedRosterAsync(spaceId);

        // Presence stays outside the cache: it is a batch read out of Redis already, and it is the
        // one part of this answer that genuinely changes second to second.
        var ids        = members.Value.Select(x => x.userId).Distinct().ToList();
        var statuses   = await userPresence.BatchGetAggregatedStatusAsync(ids);
        var activities = await userPresence.BatchGetUsersActivityPresence(ids);

        return members.Value.Select(x => new RealtimeServerMember(
            x,
            statuses.TryGetValue(x.userId, out var s) ? s : UserStatus.Offline,
            activities.TryGetValue(x.userId, out var presence) ? presence : null)).ToList();
    }

    public async Task<List<RealtimeChannel>> GetChannels()
    {
        var spaceId  = this.GetPrimaryKey();
        var callerId = this.GetUserId();

        var roster = await CachedRosterAsync(spaceId);
        var roles  = await CachedArchetypesAsync(spaceId);

        if (MemberOf(roster.Value, roles.Value, callerId) is not { } member)
            throw new InvalidOperationException($"user '{callerId}' is not a member of space '{spaceId}'");

        return await VisibleChannelsAsync((await CachedChannelsAsync(spaceId)).Value, member);
    }

    public async Task<List<ChannelGroup>> GetChannelGroups()
        => (await CachedGroupsAsync(this.GetPrimaryKey())).Value;

    /// <summary>
    /// The channels this member may see, each with its live occupancy and its current high-water mark.
    /// </summary>
    /// <remarks>
    /// The fan-out is the expensive half and it is per caller, because the visible set is. It stays
    /// out of the cache for the same reason presence does — it is the part that is different a second
    /// later.
    /// <para>
    /// <c>lastMessageId</c> is filled here rather than taken from the cached channel, because the
    /// cached one is whatever the database held when the entry was last filled, up to two minutes
    /// ago. See <see cref="LastMessageIdsAsync"/>.
    /// </para>
    /// </remarks>
    private async Task<List<RealtimeChannel>> VisibleChannelsAsync(List<CachedChannel> channels, CachedMember member)
    {
        // Rebuilt rather than cached: the evaluator reads exactly the two fields these carry, and an
        // EF graph is what the projections exist to keep out of the cache.
        var asMember = member.AsEntity();

        var visible = channels
           .Where(c => EntitlementAnalyzer.IsEntitlementSatisfied(
                EntitlementEvaluator.ApplyPermissionOverwrites(member.BasePermissions, asMember, c.AsEntity()),
                ArgonEntitlement.ViewChannel))
           .ToList();

        // Started before the fan-out and awaited after it: neither needs the other's answer, and this
        // is already the slowest call a client makes, so the Redis round trip rides along inside it
        // instead of being added to it.
        var marks = LastMessageIdsAsync(visible);

        var states = await Task.WhenAll(visible
           .Select(x => grainFactory.GetGrain<IChannelGrain>(x.Channel.channelId).GetRealtimeStateAsync()));

        var lastMessageIds = await marks;

        return visible.Select((channel, index) => new RealtimeChannel(
            channel.Channel with { lastMessageId = lastMessageIds[index] },
            new(states[index].Members))).ToList();
    }

    /// <summary>
    /// Where a channel's high-water mark lives between the activation that writes it and everyone who
    /// reads it. <c>IChannelGrain</c> writes this cell on every send.
    /// </summary>
    /// <remarks>
    /// The guid is the default ("D") form because that is what both sides get from
    /// <see cref="Guid.ToString()"/> without thinking about it. Nothing enforces that the two sides
    /// agree: change the shape on one of them and no call fails, every mark simply falls back to the
    /// database value and quietly goes back to being up to two minutes old.
    /// </remarks>
    private static string LastMessageKey(Guid channelId) => ChannelHighWaterCell.KeyFor(channelId);

    /// <summary>
    /// The freshest high-water mark for each of <paramref name="channels"/>, positionally.
    /// </summary>
    /// <remarks>
    /// One multi-key GET, not a get per channel. A bootstrap already makes one grain call per visible
    /// channel; a Redis round trip each on top of that would double the width of the request for a
    /// number that fits in eight bytes.
    /// <para>
    /// A missing cell is ordinary rather than an error — a channel nobody has posted in since the
    /// cell was last written has none, and after a Redis flush nothing does. The database row is
    /// still maintained, one flush behind rather than per send, so it is a correct floor; taking the
    /// larger of the two also
    /// covers the mirror case, where the write to the database failed but the cell landed. Both
    /// numbers only ever go up, so the maximum is always the true answer and never a guess.
    /// </para>
    /// <para>
    /// Redis being unreachable degrades instead of throwing. The database values are already in hand
    /// and are exactly what this method served before the cell existed, so failing a whole space
    /// bootstrap over a counter would be the worse trade by a wide margin.
    /// </para>
    /// </remarks>
    private async Task<long[]> LastMessageIdsAsync(List<CachedChannel> channels)
    {
        var marks = channels.Select(c => c.Channel.lastMessageId).ToArray();

        if (marks.Length == 0)
            return marks;

        try
        {
            await using var scope = redis.Rent();

            var cells = await scope.GetDatabase()
               .StringGetAsync(channels.Select(c => (RedisKey)LastMessageKey(c.Channel.channelId)).ToArray());

            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].TryParse(out long mark) && mark > marks[i])
                    marks[i] = mark;
            }

            return marks;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "could not read channel high-water marks for {Count} channels; serving the database values instead",
                channels.Count);

            return marks;
        }
    }

    /// <summary>
    /// The roster, which every caller wants in full anyway and which is what a login storm asks for
    /// once per arriving member.
    /// </summary>
    private ValueTask<Versioned<List<SpaceMember>>> CachedRosterAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:members:{spaceId}", (context, spaceId),
            static async (state, ct) =>
            {
                await using var db = await state.context.CreateDbContextAsync(ct);

                var rows = await db.UsersToServerRelations
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Include(x => x.User)
                   .Where(x => x.SpaceId == state.spaceId)
                   .Include(x => x.SpaceMemberArchetypes)
                   .ToListAsync(ct);

                return Version(rows.Select(x => x.ToDto()).ToList());
            },
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    private ValueTask<Versioned<List<ChannelGroup>>> CachedGroupsAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:groups:{spaceId}", (context, spaceId),
            static async (state, ct) =>
            {
                await using var db = await state.context.CreateDbContextAsync(ct);

                var rows = await db.Set<ChannelGroupEntity>()
                   .AsNoTracking()
                   .Where(g => g.SpaceId == state.spaceId)
                   .OrderBy(g => g.FractionalIndex)
                   .ToListAsync(ct);

                return Version(rows.Select(x => x.ToDto()).ToList());
            },
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    private ValueTask<Versioned<List<CachedChannel>>> CachedChannelsAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:channels:{spaceId}", (context, spaceId),
            static async (state, ct) =>
            {
                await using var db = await state.context.CreateDbContextAsync(ct);

                var rows = await db.Channels
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Where(c => c.SpaceId == state.spaceId)
                   .Include(c => c.EntitlementOverwrites)
                   .OrderBy(c => c.ChannelGroupId)
                   .ThenBy(c => c.FractionalIndex)
                   .ToListAsync(ct);

                var channels = rows.Select(c => new CachedChannel(c.ToDto(), c.EntitlementOverwrites
                   .Select(o => new CachedOverwrite(o.Scope, o.ArchetypeId, o.SpaceMemberId, o.Allow, o.Deny))
                   .ToList())).ToList();

                // Not Version(): the token for channels deliberately does not cover lastMessageId.
                // See CachedChannel.VersionOf.
                return new Versioned<List<CachedChannel>>(CachedChannel.VersionOf(channels), channels);
            },
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    /// <summary>
    /// The space's archetypes, over the cache that already holds them.
    /// </summary>
    /// <remarks>
    /// A cache over a cache, and on purpose: the inner one hands back a list, and what is wanted here
    /// is the list <em>and</em> its version, computed once rather than on every request. Both are
    /// dropped by the same tag, so they cannot disagree.
    /// </remarks>
    private ValueTask<Versioned<List<Archetype>>> CachedArchetypesAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:archetypes:{spaceId}", (archetypes, spaceId),
            static async (state, ct) => Version(await state.archetypes.GetAllAsync(state.spaceId, ct)),
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    private static Versioned<T> Version<T>(T value) => new(SpaceReadVersion.Of(value), value);

    /// <summary>
    /// What the channel filter needs about the caller, assembled from two lists that are already in
    /// hand rather than read back out of the database.
    /// </summary>
    /// <remarks>
    /// This used to be its own cache entry over its own query, keyed by member — which meant one
    /// database round trip per <em>distinct</em> arriving user, so a hundred and fifty people
    /// arriving at once opened a hundred and fifty of them and exhausted the connection pool before
    /// any of them was cached.
    /// <para>
    /// Nothing here needed the database. The roster already carries which archetypes each member
    /// holds, and the archetype list already carries what each archetype grants. Joining them is the
    /// whole computation.
    /// </para>
    /// </remarks>
    private static CachedMember? MemberOf(List<SpaceMember> roster, List<Archetype> granted, Guid userId)
    {
        if (roster.FirstOrDefault(m => m.userId == userId) is not { } member)
            return null;

        var held = member.archetypes.Select(a => a.archetypeId).ToList();

        return new CachedMember(member.memberId,
            granted.Where(a => held.Contains(a.id))
               .Aggregate(ArgonEntitlement.None, (permissions, a) => permissions | a.entitlement),
            held);
    }
}

/// <summary>
/// What the permission filter needs off a member, flattened.
/// </summary>
/// <remarks>
/// The entities cannot go into the cache as EF hands them over: their navigations are cyclic — a
/// member's archetype points at the archetype, whose member list points back at it — and the cache
/// serializer refuses the graph outright. Projecting also keeps the entry down to what is actually
/// read, a few hundred bytes rather than the whole object graph.
/// </remarks>
public sealed record CachedMember(Guid Id, ArgonEntitlement BasePermissions, List<Guid> ArchetypeIds)
{
    public SpaceMemberEntity AsEntity()
        => new()
        {
            Id                    = Id,
            SpaceMemberArchetypes = ArchetypeIds.Select(id => new SpaceMemberArchetypeEntity { ArchetypeId = id }).ToList()
        };
}

public sealed record CachedOverwrite(
    IArchetypeScope Scope,
    Guid? ArchetypeId,
    Guid? SpaceMemberId,
    ArgonEntitlement Allow,
    ArgonEntitlement Deny);

public sealed record CachedChannel(ArgonChannel Channel, List<CachedOverwrite> Overwrites)
{
    /// <summary>
    /// The token for a space's channel list: everything about its channels except how recently
    /// someone talked in one.
    /// </summary>
    /// <remarks>
    /// <c>lastMessageId</c> is zeroed before hashing rather than left out of the cached value,
    /// because the cached value is where the fallback for a missing Redis cell comes from — see
    /// <c>SpaceReadGrain.LastMessageIdsAsync</c> — and a second copy of the same number stored beside
    /// the DTO would only be a second thing to keep in step. Zeroing a copy of the whole record also
    /// means a field added to <see cref="CachedChannel"/> or to <see cref="ArgonChannel"/> tomorrow
    /// is covered by the token without anyone remembering to add it here; a hand-listed projection of
    /// the "stable" fields would silently stop detecting the new one.
    /// <para>
    /// Why it matters: the token gates the expensive branch of <c>GetSnapshot</c>, the one that fans
    /// out a grain call per visible channel. While the counter was inside the hash, the token changed
    /// every time the two-minute cache entry refilled in any space with traffic, so every reconnect
    /// took that branch and the versioned bootstrap saved nothing at all. Put the counter back in and
    /// the dedupe silently stops working again — it will not fail a test that only checks the answer
    /// is correct, because it still is, just expensively.
    /// </para>
    /// <para>
    /// Shipping this changes every space's channels token exactly once: the tokens clients hold were
    /// computed with the counter, so the first reconnect after the deploy re-fetches. That is one
    /// re-fetch per client in exchange for never re-fetching on a timer again.
    /// </para>
    /// </remarks>
    public static string VersionOf(List<CachedChannel> channels)
        => SpaceReadVersion.Of(channels
           .Select(c => c with { Channel = c.Channel with { lastMessageId = 0 } })
           .ToList());

    public ChannelEntity AsEntity()
        => new()
        {
            EntitlementOverwrites = Overwrites.Select(o => new ChannelEntitlementOverwriteEntity
            {
                Scope         = o.Scope,
                ArchetypeId   = o.ArchetypeId,
                SpaceMemberId = o.SpaceMemberId,
                Allow         = o.Allow,
                Deny          = o.Deny
            }).ToList()
        };
}
