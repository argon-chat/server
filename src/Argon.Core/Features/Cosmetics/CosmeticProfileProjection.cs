namespace Argon.Features.Cosmetics;

using System.Collections.Frozen;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ion.runtime;
using Orleans;

/// <summary>
/// What one person is wearing, resolved.
/// </summary>
public sealed record CosmeticProfileView(
    Guid?                          LoadoutId,
    IReadOnlyList<EquippedCosmetic> Equipped,
    int?                           BackgroundId,
    int?                           VoiceCardEffectId,
    int?                           AvatarFrameId,
    int?                           NickEffectId,
    IReadOnlyList<string>          Badges,
    CosmeticIdentity               Identity)
{
    public static CosmeticProfileView Nothing { get; } = new(null, [], null, null, null, null, [], default);
}

/// <summary>
/// What a look says a person is called and looks like here. Every field is null until it is
/// overridden, and null means the account's own value shows through.
/// </summary>
/// <remarks>
/// A look is a diff rather than a copy, so that changing an account's avatar reaches every look that
/// never claimed one of its own — copying identity into each look would mean setting a new picture
/// once per look, forever.
/// </remarks>
public readonly record struct CosmeticIdentity(string? DisplayName, string? AvatarFileId, string? Bio);

public interface ICosmeticProfileProjection
{
    /// <summary>
    /// Merges what the person is wearing into a mapped profile, including the pre-cosmetics fields
    /// an older client reads.
    /// </summary>
    ValueTask<ArgonUserProfile> ApplyAsync(ArgonUserProfile profile, Guid? spaceId, CancellationToken ct = default);

    /// <summary>
    /// The same answer for many people at once, in a fixed number of queries.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, CosmeticProfileView>> BuildManyAsync(
        IReadOnlyCollection<Guid> userIds, Guid? spaceId, CancellationToken ct = default);

    /// <summary>
    /// Drops the cached kill-switch answers so the next read asks again.
    /// </summary>
    /// <remarks>
    /// Called when an operator flips a kind, and it only reaches this process — every other replica
    /// picks the change up when its own cache expires. That is the honest bound on how fast a kind
    /// goes dark platform-wide, and it is why the cache lifetime is seconds rather than minutes.
    /// </remarks>
    void InvalidateGates();
}

/// <summary>
/// The single place a profile's cosmetics are resolved, and the only writer of the legacy fields.
/// </summary>
/// <remarks>
/// <para><b>Everything reads through here, and that is the point.</b> <c>UserProfileEntity.Map</c> is
/// a static mapper with no dependencies, so the merge cannot live inside it; instead every site that
/// produces an <c>ArgonUserProfile</c> calls <see cref="ApplyAsync"/> on the result. If one of them
/// forgets, that surface and the others disagree about the same person — which is exactly the defect
/// the profile's own wire comments describe for a different field.</para>
///
/// <para><b>Batched by construction.</b> <see cref="BuildManyAsync"/> is the primitive and the single
/// -user path calls into it, because the expensive caller is a member list: five thousand rows
/// resolved one at a time is five thousand round trips, and writing the batch later never happens.</para>
///
/// <para><b>The kind gate is evaluated platform-wide, with no user in the context.</b> A kill switch
/// has to mean the same thing for everyone looking: if a kind were rolled out per viewer, one person
/// would see a badge and the person beside them would not, on the same profile. Rolling a kind out
/// gradually is a thing to do to the client's picker, not to what other people can see.</para>
/// </remarks>
public sealed class CosmeticProfileProjection(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IGrainFactory grainFactory,
    CosmeticKindRegistry registry) : ICosmeticProfileProjection
{
    /// <summary>
    /// How long the set of cosmetic kill switches is trusted without asking again.
    /// </summary>
    /// <remarks>
    /// The flag grain caches too, so this saves a grain call per profile read rather than a database
    /// hit. Thirty seconds is the delay between an operator flipping a kind off and the server
    /// ceasing to serve it; clients hear immediately through <c>FeatureFlagActivated</c> and stop
    /// rendering, so the visible lag is nil and this only bounds how long a stale client could still
    /// be told about it.
    /// </remarks>
    private static readonly TimeSpan GateCacheTtl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim gateLock = new(1, 1);

    private FrozenDictionary<string, bool>? gates;
    private DateTimeOffset                  gatesLoadedAt = DateTimeOffset.MinValue;

    public void InvalidateGates()
    {
        gates         = null;
        gatesLoadedAt = DateTimeOffset.MinValue;
    }

    public async ValueTask<ArgonUserProfile> ApplyAsync(
        ArgonUserProfile profile, Guid? spaceId, CancellationToken ct = default)
    {
        var views = await BuildManyAsync([profile.userId], spaceId, ct);

        if (!views.TryGetValue(profile.userId, out var view) || view.LoadoutId is null)
            return profile;

        return profile with
        {
            cosmetics         = new IonArray<EquippedCosmetic>(view.Equipped.ToList()),
            loadoutId         = view.LoadoutId,

            // The look's own identity where it has one. This is what makes a look a persona rather
            // than a set of decorations: in the spaces it applies to, it is who you are.
            //
            // Carried beside the account's values rather than replacing them, because the account's
            // name is still what a search finds and what a moderator sees.
            displayNameOverride  = view.Identity.DisplayName,
            avatarFileIdOverride = view.Identity.AvatarFileId,
            bio                  = view.Identity.Bio ?? profile.bio,

            // Only overwritten when the new model has an answer. A person who has never touched the
            // new system keeps whatever their profile row already said, which is what makes this
            // release a no-op for everybody who has not opted in.
            backgroundId      = view.BackgroundId      ?? profile.backgroundId,
            voiceCardEffectId = view.VoiceCardEffectId ?? profile.voiceCardEffectId,
            avatarFrameId     = view.AvatarFrameId     ?? profile.avatarFrameId,
            nickEffectId      = view.NickEffectId      ?? profile.nickEffectId,
            badges            = new IonArray<string>(view.Badges.ToList())
        };
    }

    public async ValueTask<IReadOnlyDictionary<Guid, CosmeticProfileView>> BuildManyAsync(
        IReadOnlyCollection<Guid> userIds, Guid? spaceId, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, CosmeticProfileView>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var loadouts = await ResolveLoadoutsAsync(db, userIds, spaceId, ct);

        if (loadouts.Count == 0)
            return new Dictionary<Guid, CosmeticProfileView>();

        // Read once for the whole batch: a member list asks about hundreds of people at a time and a
        // name is wanted for every one of them, worn cosmetics or not.
        var identities = await ResolveIdentitiesAsync(db, loadouts.Values.ToList(), ct);

        var loadoutIds = loadouts.Values.ToList();

        // Left, not inner: a bare kind's row points at no catalogue item, and an inner join would
        // drop it — silently, and only for the kinds that have nothing to sell.
        var worn = await db.CosmeticEquips
           .Where(equip => loadoutIds.Contains(equip.LoadoutId))
           .Select(equip => new
            {
                Equip = equip,
                Item  = db.Cosmetics.FirstOrDefault(item => item.Id == equip.CosmeticItemId)
            })
           .ToListAsync(ct);

        if (worn.Count == 0)
            return Empty(loadouts, identities);

        var wearers = loadouts.Keys.ToList();

        var owned = await db.CosmeticOwnerships
           .Where(x => wearers.Contains(x.UserId) && x.RevokedAt == null)
           .Select(x => new
            {
                x.UserId,
                x.CosmeticItemId,
                x.ExpiresAt
            })
           .ToListAsync(ct);

        var premium = await db.Users
           .Where(x => wearers.Contains(x.Id) && x.HasActiveUltima)
           .Select(x => x.Id)
           .ToListAsync(ct);

        // Badges a person held and no longer does. Subtracted from the legacy array, because that
        // array is unioned with whatever the profile row still carries and a revocation has to reach
        // it — there is no other writer that would remove one.
        var revoked = await db.CosmeticOwnerships
           .Where(x => wearers.Contains(x.UserId) && x.RevokedAt != null)
           .Join(db.Cosmetics, ownership => ownership.CosmeticItemId, item => item.Id,
                (ownership, item) => new
                {
                    ownership.UserId,
                    item.Slug
                })
           .ToListAsync(ct);

        var stored = await db.UserProfiles
           .Where(x => wearers.Contains(x.UserId))
           .Select(x => new
            {
                x.UserId,
                x.Badges
            })
           .ToListAsync(ct);

        var gateByKind = await ResolveGatesAsync(ct);
        var now        = DateTimeOffset.UtcNow;
        var optionRows = await CosmeticComposition.ResolveRowsAsync(db, registry, worn.Select(x => x.Equip), ct);

        var ownedByUser   = Group(owned, x => x.UserId, x => (x.CosmeticItemId, x.ExpiresAt));
        var revokedByUser = Group(revoked, x => x.UserId, x => x.Slug);
        var premiumUsers  = premium.ToHashSet();
        var wornByLoadout = Group(worn, x => x.Equip.LoadoutId, x => x);
        var storedByUser  = stored.ToDictionary(x => x.UserId, x => x.Badges);

        var result = new Dictionary<Guid, CosmeticProfileView>(loadouts.Count);

        foreach (var (userId, loadoutId) in loadouts)
        {
            var ownedItems = ownedByUser.GetValueOrDefault(userId) ?? [];
            var hasPremium = premiumUsers.Contains(userId);
            var rows       = wornByLoadout.GetValueOrDefault(loadoutId) ?? [];

            var equipped = new List<EquippedCosmetic>(rows.Count);
            var badges   = new List<string>();

            int? background = null;
            int? voiceCard  = null;
            int? avatarFrame = null;
            int? nickEffect = null;

            foreach (var row in rows.OrderBy(x => x.Equip.KindKey).ThenBy(x => x.Equip.SlotIndex))
            {
                if (!registry.TryGet(row.Equip.KindKey, out var kind))
                    continue;

                if (!gateByKind.GetValueOrDefault(row.Equip.KindKey, true))
                    continue;

                // A bare kind has no row to be servable or owned: what is owned are the options it
                // composes, and those are checked one by one below.
                if (row.Item is { } item && (!IsServable(item, now) || !CanWear(kind, item, ownedItems, hasPremium, now)))
                    continue;

                equipped.Add(new EquippedCosmetic(
                    row.Equip.KindKey,
                    row.Item?.Id ?? Guid.Empty,
                    row.Item?.Slug ?? string.Empty,
                    kind.Layer,
                    row.Equip.SlotIndex,
                    row.Item?.Payload ?? "{}",
                    new IonArray<EquippedCosmeticAsset>(
                        row.Item is null ? [] : CosmeticComposition.AssetsOf(row.Item)),
                    new IonArray<EquippedCosmeticOption>(CosmeticComposition.Compose(
                        row.Equip, kind, registry, optionRows,
                        // Re-checked rather than trusted. Equip already refused anything they were not
                        // entitled to, but entitlement lapses: a face held by a subscription stops
                        // being theirs when the subscription ends, and an option an operator switched
                        // off stops being anybody's. Checking here is what makes both take effect with
                        // no row deleted, and what lets switching either back restore the name.
                        (optionKind, option) =>
                            gateByKind.GetValueOrDefault(option.KindKey, true)
                            && IsServable(option, now)
                            && CanWear(optionKind, option, ownedItems, hasPremium, now))),
                    row.Equip.Content,
                    row.Equip.BoardX,
                    row.Equip.BoardY,
                    row.Equip.BoardW,
                    row.Equip.BoardH,

                    // Which authoring of the row this is. A profile is cached on the client for
                    // hours, so an operator re-authoring a published cosmetic reaches nobody holding
                    // one — nothing about the person changed, so no event is raised for them. The
                    // catalogue carries the same number, and comparing the two is what lets a client
                    // drop exactly the profiles that are behind.
                    row.Item?.Version));

                if (row.Item is null)
                    continue;

                switch (kind.LegacyField)
                {
                    case LegacyCosmeticField.BackgroundId:
                        background ??= row.Item.LegacyId;
                        break;
                    case LegacyCosmeticField.VoiceCardEffectId:
                        voiceCard ??= row.Item.LegacyId;
                        break;
                    case LegacyCosmeticField.AvatarFrameId:
                        avatarFrame ??= row.Item.LegacyId;
                        break;
                    case LegacyCosmeticField.NickEffectId:
                        nickEffect ??= row.Item.LegacyId;
                        break;
                    case LegacyCosmeticField.Badges:
                        badges.Add(row.Item.Slug);
                        break;
                }
            }

            equipped.Sort((left, right) => left.layer.CompareTo(right.layer));

            result[userId] = new CosmeticProfileView(
                loadoutId,
                equipped,
                background,
                voiceCard,
                avatarFrame,
                nickEffect,
                MergeBadges(storedByUser.GetValueOrDefault(userId), badges, revokedByUser.GetValueOrDefault(userId)),
                identities.GetValueOrDefault(loadoutId));
        }

        return result;
    }

    /// <summary>
    /// Which loadout applies to each person here: the one assigned to this space, else the one
    /// assigned everywhere.
    /// </summary>
    /// <remarks>
    /// <b>An assignment is the only thing that puts a look somewhere.</b> There used to be a third
    /// tier below these, the loadout's own <c>IsDefault</c> flag, and it was a second way of saying
    /// "everywhere" that the person could not see and the scope picker did not set: somebody who
    /// chose one space for a look still wore it in every other space, because the look happened to
    /// be the first one they ever made. <c>IsDefault</c> now marks the look that holds the global
    /// assignment and nothing else, so what the picker says is what happens.
    /// </remarks>
    private static async Task<Dictionary<Guid, Guid>> ResolveLoadoutsAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> userIds, Guid? spaceId, CancellationToken ct)
    {
        var ids = userIds.ToList();

        var candidates = await db.CosmeticLoadouts
            // A look put away is worn nowhere, whatever it is assigned to — so it is not a candidate
            // at all, and the spaces it holds fall through as if it had never claimed them.
           .Where(loadout => ids.Contains(loadout.UserId) && !loadout.IsPaused)
           .Select(loadout => new
            {
                loadout.UserId,
                LoadoutId = loadout.Id,
                ForSpace = db.CosmeticScopeAssignments
                   .Any(a => a.UserId == loadout.UserId && a.SpaceId == spaceId && a.LoadoutId == loadout.Id),
                ForGlobal = db.CosmeticScopeAssignments
                   .Any(a => a.UserId == loadout.UserId && a.SpaceId == null && a.LoadoutId == loadout.Id)
            })
           .ToListAsync(ct);

        var resolved = new Dictionary<Guid, Guid>(candidates.Count);
        var rank     = new Dictionary<Guid, int>(candidates.Count);

        foreach (var candidate in candidates)
        {
            // A space-specific assignment beats a global one. Ranked rather than ordered in SQL
            // because a null spaceId makes the two the same row, and a database-side ordering that
            // has to encode that reads worse than this does.
            var precedence = candidate.ForSpace && spaceId is not null ? 0
                : candidate.ForGlobal                                  ? 1
                : int.MaxValue;

            if (precedence is int.MaxValue)
                continue;

            if (rank.TryGetValue(candidate.UserId, out var held) && held <= precedence)
                continue;

            rank[candidate.UserId]     = precedence;
            resolved[candidate.UserId] = candidate.LoadoutId;
        }

        return resolved;
    }

    /// <summary>
    /// Whether the catalogue row may be shown at all — the per-item switch and its availability
    /// window, which are the operator's business rather than the wearer's.
    /// </summary>
    /// <remarks>
    /// The rule itself lives in <see cref="CosmeticAvailability"/>, because the catalogue, equipping
    /// and a key being spent all have to answer it the same way as this does.
    /// </remarks>
    private static bool IsServable(CosmeticItemEntity item, DateTimeOffset now)
        => CosmeticAvailability.IsServable(item, now);

    private static bool CanWear(
        CosmeticKindDefinition kind,
        CosmeticItemEntity item,
        IReadOnlyList<(Guid ItemId, DateTimeOffset? ExpiresAt)> owned,
        bool hasPremium,
        DateTimeOffset now)
    {
        if (kind.Entitlement is CosmeticEntitlement.Free || item.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.Free))
            return true;

        foreach (var (itemId, expiresAt) in owned)
        {
            if (itemId == item.Id && (expiresAt is null || expiresAt > now))
                return true;
        }

        // Subscription-held, which is the case with no row behind it: the entitlement is the live
        // subscription, so it ends the moment the subscription does and needs nothing deleted.
        return kind.Entitlement is CosmeticEntitlement.UltimaOrOwned
               && hasPremium
               && item.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.UltimaTier);
    }

    /// <summary>
    /// The legacy badge array: what the profile row carries, plus what is worn, minus what was taken
    /// away.
    /// </summary>
    /// <remarks>
    /// The union is what lets the migration be lazy. Badges granted through the inventory path still
    /// land in <c>UserProfileEntity.Badges</c> and still show, so nothing had to be moved for this
    /// release; the subtraction is the part that did not exist before, because
    /// <c>InventoryGrain.AddBadgeToProfileAsync</c> has never had a counterpart.
    /// </remarks>
    private static List<string> MergeBadges(List<string>? stored, List<string> worn, List<string>? revoked)
    {
        var merged = new List<string>((stored?.Count ?? 0) + worn.Count);
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var gone   = revoked is null ? null : new HashSet<string>(revoked, StringComparer.Ordinal);

        if (stored is not null)
        {
            foreach (var badge in stored)
            {
                if (gone is not null && gone.Contains(badge))
                    continue;

                if (seen.Add(badge))
                    merged.Add(badge);
            }
        }

        foreach (var badge in worn)
        {
            if (seen.Add(badge))
                merged.Add(badge);
        }

        return merged;
    }

    private async ValueTask<FrozenDictionary<string, bool>> ResolveGatesAsync(CancellationToken ct)
    {
        if (gates is not null && DateTimeOffset.UtcNow - gatesLoadedAt < GateCacheTtl)
            return gates;

        await gateLock.WaitAsync(ct);

        try
        {
            if (gates is not null && DateTimeOffset.UtcNow - gatesLoadedAt < GateCacheTtl)
                return gates;

            var flagGrain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
            var declared  = registry.All.Select(kind => kind.FeatureFlagKey).ToHashSet(StringComparer.Ordinal);

            var existing = new List<string>();

            foreach (var flag in await flagGrain.ListFlagsAsync())
            {
                if (declared.Contains(flag.Id))
                    existing.Add(flag.Id);
            }

            var resolved = new Dictionary<string, bool>(registry.All.Count, StringComparer.Ordinal);

            // Nobody has ever switched a kind off, which is the normal state: every kind is on and
            // there is nothing to evaluate.
            if (existing.Count == 0)
            {
                foreach (var kind in registry.All)
                {
                    resolved[kind.Key] = true;
                }
            }
            else
            {
                var evaluated = await flagGrain.EvaluateManyAsync(existing, FeatureFlagEvaluationContext.Empty);

                foreach (var kind in registry.All)
                {
                    var present = evaluated.TryGetValue(kind.FeatureFlagKey, out var verdict);

                    resolved[kind.Key] = CosmeticKindGate.IsEnabled(present, verdict?.IsEnabled ?? true);
                }
            }

            gates         = resolved.ToFrozenDictionary(StringComparer.Ordinal);
            gatesLoadedAt = DateTimeOffset.UtcNow;

            return gates;
        }
        finally
        {
            gateLock.Release();
        }
    }

    private static Dictionary<Guid, CosmeticProfileView> Empty(
        Dictionary<Guid, Guid> loadouts, IReadOnlyDictionary<Guid, CosmeticIdentity> identities)
    {
        var empty = new Dictionary<Guid, CosmeticProfileView>(loadouts.Count);

        foreach (var (userId, loadoutId) in loadouts)
        {
            // A look with nothing equipped can still be somebody else: a different name is the whole
            // point of one, and wearing nothing is not the same as being nobody.
            empty[userId] = CosmeticProfileView.Nothing with
            {
                LoadoutId = loadoutId,
                Identity  = identities.GetValueOrDefault(loadoutId)
            };
        }

        return empty;
    }

    private static async Task<Dictionary<Guid, CosmeticIdentity>> ResolveIdentitiesAsync(
        ApplicationDbContext db, List<Guid> loadoutIds, CancellationToken ct)
    {
        var rows = await db.CosmeticLoadouts
           .Where(loadout => loadoutIds.Contains(loadout.Id))
           .Select(loadout => new
            {
                loadout.Id,
                loadout.DisplayNameOverride,
                loadout.AvatarFileIdOverride,
                loadout.BioOverride
            })
           .ToListAsync(ct);

        var identities = new Dictionary<Guid, CosmeticIdentity>(rows.Count);

        foreach (var row in rows)
        {
            identities[row.Id] = new CosmeticIdentity(
                row.DisplayNameOverride, row.AvatarFileIdOverride, row.BioOverride);
        }

        return identities;
    }

    private static Dictionary<Guid, List<TValue>> Group<TSource, TValue>(
        List<TSource> source, Func<TSource, Guid> key, Func<TSource, TValue> value)
    {
        var grouped = new Dictionary<Guid, List<TValue>>();

        foreach (var item in source)
        {
            var id = key(item);

            if (!grouped.TryGetValue(id, out var bucket))
            {
                bucket      = [];
                grouped[id] = bucket;
            }

            bucket.Add(value(item));
        }

        return grouped;
    }
}
