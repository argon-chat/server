namespace Argon.Api.Grains;

using Argon.Core.Features.Transport;
using Argon.Entities;
using Argon.Api.Grains.Interfaces;
using Argon.Features.Cosmetics;
using Argon.Features.Moderation;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ion.runtime;
using Orleans;
using Orleans.Concurrency;

/// <summary>
/// One person's cosmetics.
/// </summary>
/// <remarks>
/// <para><b>Reads are resolved here so that no caller has to know the rules.</b> Whether somebody may
/// wear a thing depends on the catalogue row, the kind's entitlement, an ownership row that may not
/// exist because a subscription covers it, and a kill switch — and the answer has to be the same
/// whether it is asked by the picker, by a profile card or by a member list.</para>
///
/// <para><b>Every write broadcasts.</b> Equipping is a change other people can see, so it goes out as
/// <c>UserProfileUpdated</c> to every space the person is in, exactly as a profile edit does. Without
/// it a new look would appear only to whoever happened to refetch, and the client's profile cache
/// holds for three hours.</para>
/// </remarks>
[StatelessWorker]
public class CosmeticsGrain(
    IDbContextFactory<ApplicationDbContext> context,
    CosmeticKindRegistry registry,
    ICosmeticProfileProjection projection,
    AppHubServer appHubServer,
    ILogger<ICosmeticsGrain> logger) : Grain, ICosmeticsGrain
{
    /// <summary>
    /// How many personas one account may keep. A limit rather than none, because each one is read on
    /// every profile resolution and somebody would otherwise find out how many is too many for us.
    /// </summary>
    private const int LoadoutLimit = 12;

    /// <summary>How many people one batch may ask about. A rendered window, not a whole roster.</summary>
    private const int WornByLimit = 500;

    public async Task<CosmeticCatalogue> GetCatalogueAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        var published = await ctx.Cosmetics
           .AsNoTracking()
           .Where(CosmeticAvailability.ServableAt(now))
           .OrderBy(x => x.KindKey)
           .ThenBy(x => x.SortOrder)
           .ToListAsync(ct);

        var publishedIds = published.Select(x => x.Id).ToList();

        // One query for every row on the page rather than one per row: the catalogue is unpaged
        // while it is small, and a per-item read would be a hundred round trips to render a picker.
        var text = await ctx.CosmeticTranslations
           .AsNoTracking()
           .Where(x => publishedIds.Contains(x.CosmeticItemId))
           .ToListAsync(ct);

        var textByItem = text
           .GroupBy(x => x.CosmeticItemId)
           .ToDictionary(x => x.Key, x => x.ToList());

        var owned      = await ActiveOwnershipAsync(ctx, userId, now, ct);
        var hasPremium = await ctx.Users.AnyAsync(x => x.Id == userId && x.HasActiveUltima, ct);

        var kinds = new List<CosmeticKindSummary>(registry.All.Count);
        var items = new List<CatalogueCosmetic>(published.Count);

        foreach (var kind in registry.All)
        {
            kinds.Add(Describe(kind));
        }

        foreach (var item in published)
        {
            if (!registry.TryGet(item.KindKey, out var kind))
            {
                // Said out loud, because otherwise it is not said at all.
                //
                // A published row of a kind this build has no file for vanishes here, and every
                // symptom of that is somewhere else: the picker opens with nothing in it, the
                // console shows the row as published and fine, and the database agrees. The usual
                // cause is the most boring one — the kind file is newer than the running host, and
                // the registry is scanned once at startup.
                logger.LogWarning(
                    "Catalogue is dropping published cosmetic {Slug} ({CosmeticId}): this build has no kind {KindKey}. "
                  + "If the kind was added since this host started, restart it.",
                    item.Slug, item.Id, item.KindKey);

                continue;
            }

            items.Add(new CatalogueCosmetic(
                item.Id,
                item.KindKey,
                item.Slug,
                item.NameKey,
                item.DescriptionKey,
                item.Rarity,
                item.Version,
                item.Payload,
                Assets(item),
                Owns(kind, item, owned, hasPremium),
                item.AvailableUntil,
                new IonArray<CosmeticAcquisition>(Acquisitions(item.AcquisitionMode)),
                Describe(CosmeticBoardOffer.Resolve(kind, item)),
                Text(textByItem, item.Id)));
        }

        return new CosmeticCatalogue(
            new IonArray<CosmeticKindSummary>(kinds),
            new IonArray<CatalogueCosmetic>(items));
    }

    public async Task<MyCosmetics> GetMyCosmeticsAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;

        var held = await ctx.CosmeticOwnerships
           .AsNoTracking()
           .Where(x => x.UserId == userId && x.RevokedAt == null)
           .Where(x => x.ExpiresAt == null || x.ExpiresAt > now)
           .Join(ctx.Cosmetics, ownership => ownership.CosmeticItemId, item => item.Id,
                (ownership, item) => new
                {
                    ownership.ExpiresAt,
                    Item = item
                })
           .ToListAsync(ct);

        var items = new List<OwnedCosmetic>(held.Count);
        var seen  = new HashSet<Guid>();

        foreach (var row in held)
        {
            seen.Add(row.Item.Id);
            items.Add(new OwnedCosmetic(row.Item.Id, row.Item.KindKey, row.Item.Slug, row.ExpiresAt, false));
        }

        if (await ctx.Users.AnyAsync(x => x.Id == userId && x.HasActiveUltima, ct))
        {
            var covered = await ctx.Cosmetics
               .AsNoTracking()
               .Where(x => x.IsPublished && x.IsEnabled)
               .Where(x => x.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.UltimaTier))
               .ToListAsync(ct);

            foreach (var item in covered)
            {
                if (seen.Add(item.Id))
                    items.Add(new OwnedCosmetic(item.Id, item.KindKey, item.Slug, null, true));
            }
        }

        return new MyCosmetics(new IonArray<OwnedCosmetic>(items));
    }

    public async Task<CosmeticLoadoutList> GetMyLoadoutsAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadouts = await ctx.CosmeticLoadouts
           .AsNoTracking()
           .Where(x => x.UserId == userId)
           .OrderBy(x => x.SortOrder)
           .ThenBy(x => x.Name)
           .ToListAsync(ct);

        if (loadouts.Count == 0)
        {
            return new CosmeticLoadoutList(
                IonArray<CosmeticLoadout>.Empty,
                IonArray<CosmeticScopeAssignment>.Empty);
        }

        var loadoutIds = loadouts.Select(x => x.Id).ToList();

        // Left, not inner: a bare kind's row points at no catalogue item and an inner join drops it.
        var worn = await ctx.CosmeticEquips
           .AsNoTracking()
           .Where(x => loadoutIds.Contains(x.LoadoutId))
           .Select(equip => new
            {
                Equip = equip,
                Item  = ctx.Cosmetics.FirstOrDefault(item => item.Id == equip.CosmeticItemId)
            })
           .ToListAsync(ct);

        var assignments = await ctx.CosmeticScopeAssignments
           .AsNoTracking()
           .Where(x => x.UserId == userId)
           .Select(x => new CosmeticScopeAssignment(x.SpaceId, x.LoadoutId))
           .ToListAsync(ct);

        var optionRows = await CosmeticComposition.ResolveRowsAsync(ctx, registry, worn.Select(x => x.Equip), ct);

        var described = new List<CosmeticLoadout>(loadouts.Count);

        foreach (var loadout in loadouts)
        {
            var equipped = new List<EquippedCosmetic>();

            foreach (var row in worn)
            {
                if (row.Equip.LoadoutId != loadout.Id || !registry.TryGet(row.Equip.KindKey, out var kind))
                    continue;

                // Unfiltered, unlike the projection: this is the wearer's own wardrobe, and an option
                // they chose while a subscription covered it should read as locked in the picker
                // rather than as never having been chosen.
                equipped.Add(new EquippedCosmetic(
                    row.Equip.KindKey,
                    row.Item?.Id ?? Guid.Empty,
                    row.Item?.Slug ?? string.Empty,
                    kind.Layer,
                    row.Equip.SlotIndex,
                    row.Item?.Payload ?? "{}",
                    row.Item is null ? IonArray<EquippedCosmeticAsset>.Empty : Assets(row.Item),
                    new IonArray<EquippedCosmeticOption>(
                        CosmeticComposition.Compose(row.Equip, kind, registry, optionRows)),
                    row.Equip.Content,
                    row.Equip.BoardX,
                    row.Equip.BoardY,
                    row.Equip.BoardW,
                    row.Equip.BoardH,

                    // Carried here too: the wardrobe is read straight from the database, so it is
                    // already current, and sending the number keeps one shape for both readers.
                    row.Item?.Version));
            }

            equipped.Sort((left, right) => left.layer.CompareTo(right.layer));

            described.Add(new CosmeticLoadout(
                loadout.Id, loadout.Name, loadout.IsDefault, loadout.SortOrder,
                new IonArray<EquippedCosmetic>(equipped),
                loadout.DisplayNameOverride,
                loadout.AvatarFileIdOverride,
                loadout.BioOverride,
                loadout.IsPaused));
        }

        return new CosmeticLoadoutList(
            new IonArray<CosmeticLoadout>(described),
            new IonArray<CosmeticScopeAssignment>(assignments));
    }

    public async Task<Either<CosmeticLoadoutCreated, CosmeticError>> CreateLoadoutAsync(string name, CancellationToken ct = default)
    {
        var userId  = this.GetPrimaryKey();
        var trimmed = name.Trim();

        if (trimmed.Length is 0 or > 64)
            return CosmeticError.NAME_INVALID;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var existing = await ctx.CosmeticLoadouts.Where(x => x.UserId == userId).ToListAsync(ct);

        if (existing.Count >= LoadoutLimit)
            return CosmeticError.LOADOUT_LIMIT;

        if (existing.Any(x => string.Equals(x.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            return CosmeticError.NAME_TAKEN;

        var first = existing.Count == 0;

        var loadout = new CosmeticLoadoutEntity
        {
            Id     = ArgonId.New(),
            UserId = userId,
            Name   = trimmed,

            IsDefault = first,
            SortOrder = existing.Count
        };

        ctx.CosmeticLoadouts.Add(loadout);

        // The first look is worn everywhere, and that is written as the assignment it is rather than
        // left to the flag: the flag says which look holds the global row, and a look nobody has
        // assigned anywhere is worn nowhere.
        if (first)
        {
            ctx.CosmeticScopeAssignments.Add(new CosmeticScopeAssignmentEntity
            {
                Id        = ArgonId.New(),
                UserId    = userId,
                SpaceId   = null,
                LoadoutId = loadout.Id
            });
        }

        await ctx.SaveChangesAsync(ct);

        return new CosmeticLoadoutCreated(loadout.Id);
    }

    public async Task<CosmeticError?> RenameLoadoutAsync(Guid loadoutId, string name, CancellationToken ct = default)
    {
        var userId  = this.GetPrimaryKey();
        var trimmed = name.Trim();

        if (trimmed.Length is 0 or > 64)
            return CosmeticError.NAME_INVALID;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadout = await ctx.CosmeticLoadouts.FirstOrDefaultAsync(x => x.Id == loadoutId && x.UserId == userId, ct);

        if (loadout is null)
            return CosmeticError.NOT_FOUND;

        var taken = await ctx.CosmeticLoadouts
           .AnyAsync(x => x.UserId == userId && x.Id != loadoutId && x.Name.ToLower() == trimmed.ToLower(), ct);

        if (taken)
            return CosmeticError.NAME_TAKEN;

        loadout.Name = trimmed;
        await ctx.SaveChangesAsync(ct);

        return null;
    }

    /// <summary>
    /// Moves the look worn everywhere to another persona.
    /// </summary>
    /// <remarks>
    /// <para>The same thing as assigning the global scope, said the other way round — so it writes
    /// that row too. The flag on its own decides nothing: a look is worn where it is assigned, and
    /// <c>IsDefault</c> is the name the rest of the product knows the global assignment by.</para>
    ///
    /// <para>Every row goes in one SaveChanges, because the unique index allows exactly one default
    /// per person: clearing the old one and setting the new one in two commits would leave a moment
    /// with none.</para>
    /// </remarks>
    public async Task<CosmeticError?> SetDefaultLoadoutAsync(Guid loadoutId, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        if (!await ctx.CosmeticLoadouts.AnyAsync(x => x.Id == loadoutId && x.UserId == userId, ct))
            return CosmeticError.NOT_FOUND;

        await MarkDefaultAsync(ctx, userId, loadoutId, ct);
        await SetGlobalAssignmentAsync(ctx, userId, loadoutId, ct);

        await ctx.SaveChangesAsync(ct);
        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    /// <summary>
    /// Points the global scope at one look, or at none, without saving.
    /// </summary>
    /// <remarks>
    /// The caller saves, so the row and the flag it mirrors land together. Nothing reads the flag to
    /// decide where a look is worn — it is kept only so a list of looks can say which one is the
    /// one worn everywhere without a second query.
    /// </remarks>
    private static async Task SetGlobalAssignmentAsync(
        ApplicationDbContext ctx, Guid userId, Guid? loadoutId, CancellationToken ct)
    {
        var assignment = await ctx.CosmeticScopeAssignments
           .FirstOrDefaultAsync(x => x.UserId == userId && x.SpaceId == null, ct);

        if (loadoutId is not { } chosen)
        {
            if (assignment is not null)
                ctx.CosmeticScopeAssignments.Remove(assignment);

            return;
        }

        if (assignment is null)
        {
            ctx.CosmeticScopeAssignments.Add(new CosmeticScopeAssignmentEntity
            {
                Id        = ArgonId.New(),
                UserId    = userId,
                SpaceId   = null,
                LoadoutId = chosen
            });

            return;
        }

        assignment.LoadoutId = chosen;
    }

    /// <summary>
    /// Puts a look away, or takes it out again.
    /// </summary>
    /// <remarks>
    /// Its assignments are left exactly where they are. That is the point: the only way to stop
    /// wearing something used to be taking every space off it one at a time, which meant the list of
    /// spaces was doing duty as an on switch and was lost every time it was switched off.
    /// </remarks>
    public async Task<CosmeticError?> SetLoadoutPausedAsync(Guid loadoutId, bool paused, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadout = await ctx.CosmeticLoadouts.FirstOrDefaultAsync(x => x.Id == loadoutId && x.UserId == userId, ct);

        if (loadout is null)
            return CosmeticError.NOT_FOUND;

        if (loadout.IsPaused == paused)
            return null;

        loadout.IsPaused = paused;
        await ctx.SaveChangesAsync(ct);

        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    public async Task<CosmeticError?> DeleteLoadoutAsync(Guid loadoutId, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadout = await ctx.CosmeticLoadouts.FirstOrDefaultAsync(x => x.Id == loadoutId && x.UserId == userId, ct);

        if (loadout is null)
            return CosmeticError.NOT_FOUND;

        // Deleting the look worn everywhere while other looks exist would silently strip every space
        // that has not chosen one. Move it first — that is a decision, not a side effect.
        //
        // Deleting the LAST one is fine, and the distinction matters: the first look a person
        // creates is also the default, so a flat "the default cannot be deleted" locked them into
        // whatever they made first, forever. No looks at all is the state every account starts in
        // and the read path already handles it — it resolves to wearing nothing.
        if (loadout.IsDefault && await ctx.CosmeticLoadouts.AnyAsync(x => x.UserId == userId && x.Id != loadoutId, ct))
            return CosmeticError.CANNOT_DELETE_DEFAULT;

        ctx.CosmeticLoadouts.Remove(loadout);
        await ctx.SaveChangesAsync(ct);

        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    public async Task<Either<ArgonUserProfile, CosmeticError>> EquipAsync(
        Guid loadoutId, Guid cosmeticId, int slotIndex, string? overridesJson, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadout = await ctx.CosmeticLoadouts.FirstOrDefaultAsync(x => x.Id == loadoutId && x.UserId == userId, ct);

        if (loadout is null)
            return CosmeticError.NOT_FOUND;

        var item = await ctx.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

        if (item is null)
            return CosmeticError.NOT_FOUND;

        if (!registry.TryGet(item.KindKey, out var kind))
            return CosmeticError.UNKNOWN_KIND;

        // An option is chosen on somebody's axis, not worn on its own — there is no surface for it
        // to appear on, so equipping one would be a row nothing would ever draw.
        if (kind.IsCompositional)
            return CosmeticError.ITEM_UNAVAILABLE;

        // A bare kind has no rows, so an item claiming to be one of them is somebody else's.
        if (kind.IsBare)
            return CosmeticError.ITEM_UNAVAILABLE;

        var now = DateTimeOffset.UtcNow;

        if (!CosmeticAvailability.IsServable(item, now))
            return CosmeticError.ITEM_UNAVAILABLE;

        if (slotIndex < 0 || slotIndex >= kind.MaxSlots)
            return CosmeticError.SLOT_OUT_OF_RANGE;

        var owned      = await ActiveOwnershipAsync(ctx, userId, now, ct);
        var hasPremium = await ctx.Users.AnyAsync(x => x.Id == userId && x.HasActiveUltima, ct);

        if (!Owns(kind, item, owned, hasPremium))
            return CosmeticError.NOT_OWNED;

        var refusal = await CheckChoicesAsync(ctx, kind, overridesJson, owned, hasPremium, now, ct);

        if (refusal is { } problem)
        {
            logger.LogWarning("Refused axis choices on {Kind} for {User}: {Problem}", item.KindKey, userId, problem);
            return problem;
        }

        var existing = await ctx.CosmeticEquips
           .FirstOrDefaultAsync(x => x.LoadoutId == loadoutId && x.KindKey == item.KindKey && x.SlotIndex == slotIndex, ct);

        if (existing is null)
        {
            ctx.CosmeticEquips.Add(new CosmeticEquipEntity
            {
                Id             = ArgonId.New(),
                LoadoutId      = loadoutId,
                CosmeticItemId = cosmeticId,
                KindKey        = item.KindKey,
                SlotIndex      = slotIndex,
                Overrides      = string.IsNullOrWhiteSpace(overridesJson) ? null : overridesJson
            });
        }
        else
        {
            existing.CosmeticItemId = cosmeticId;
            existing.Overrides      = string.IsNullOrWhiteSpace(overridesJson) ? null : overridesJson;
        }

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Equipped {Cosmetic} ({Kind}) in slot {Slot} for {User}",
            item.Slug, item.KindKey, slotIndex, userId);

        return await BroadcastAsync(ctx, userId, ct);
    }

    /// <summary>
    /// Writes the parts of something worn that are its wearer's to decide: the axes, and whatever the
    /// kind lets them fill in.
    /// </summary>
    /// <remarks>
    /// <para>A call of its own rather than more arguments on <c>Equip</c>, because adding one to a
    /// method that has shipped is how installed builds stop being able to call it.</para>
    ///
    /// <para><b>It is also the only way a bare kind is worn.</b> A kind with no catalogue rows has
    /// nothing to equip — configuring it <i>is</i> putting it on — so for one of those this creates
    /// the row, and taking it off is an ordinary unequip.</para>
    ///
    /// <para>The content is held to the schema the kind declares, by the same validator and the same
    /// length cap as a board card's content, because it is the same column and the same idea.</para>
    /// </remarks>
    public async Task<Either<ArgonUserProfile, CosmeticError>> ConfigureCosmeticAsync(
        Guid loadoutId, string kindKey, int slotIndex, string? overridesJson, string? contentJson,
        CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        if (!await ctx.CosmeticLoadouts.AnyAsync(x => x.Id == loadoutId && x.UserId == userId, ct))
            return CosmeticError.NOT_FOUND;

        if (!registry.TryGet(kindKey, out var kind))
            return CosmeticError.UNKNOWN_KIND;

        if (slotIndex < 0 || slotIndex >= kind.MaxSlots)
            return CosmeticError.SLOT_OUT_OF_RANGE;

        var validation = kind.ValidateContent(contentJson);

        if (!validation.IsValid)
        {
            logger.LogWarning("Refused tuning for {Kind} on {Loadout}: {Problems}",
                kindKey, loadoutId, string.Join("; ", validation.Errors));
            return CosmeticError.OVERRIDE_INVALID;
        }

        var now        = DateTimeOffset.UtcNow;
        var owned      = await ActiveOwnershipAsync(ctx, userId, now, ct);
        var hasPremium = await ctx.Users.AnyAsync(x => x.Id == userId && x.HasActiveUltima, ct);

        if (await CheckChoicesAsync(ctx, kind, overridesJson, owned, hasPremium, now, ct) is { } refused)
            return refused;

        var equip = await ctx.CosmeticEquips
           .FirstOrDefaultAsync(x => x.LoadoutId == loadoutId && x.KindKey == kindKey && x.SlotIndex == slotIndex, ct);

        if (equip is null)
        {
            // Only a bare kind may be brought into being this way. For everything else there is a row
            // in the catalogue to put on first, and writing settings for an empty slot would leave
            // them behind for a thing nobody is wearing.
            if (!kind.IsBare)
                return CosmeticError.NOT_FOUND;

            equip = new CosmeticEquipEntity
            {
                Id        = ArgonId.New(),
                LoadoutId = loadoutId,
                KindKey   = kindKey,
                SlotIndex = slotIndex
            };

            ctx.CosmeticEquips.Add(equip);
        }

        equip.Overrides = string.IsNullOrWhiteSpace(overridesJson) ? null : overridesJson;
        equip.Content   = string.IsNullOrWhiteSpace(contentJson) ? null : contentJson;

        await ctx.SaveChangesAsync(ct);

        return await BroadcastAsync(ctx, userId, ct);
    }

    public async Task<Either<ArgonUserProfile, CosmeticError>> UnequipAsync(
        Guid loadoutId, string kindKey, int slotIndex, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        if (!await ctx.CosmeticLoadouts.AnyAsync(x => x.Id == loadoutId && x.UserId == userId, ct))
            return CosmeticError.NOT_FOUND;

        var equip = await ctx.CosmeticEquips
           .FirstOrDefaultAsync(x => x.LoadoutId == loadoutId && x.KindKey == kindKey && x.SlotIndex == slotIndex, ct);

        // Taking off something that is not on is not an error — the end state is what was asked for.
        if (equip is null)
            return await ReadProfileAsync(ctx, userId, ct);

        ctx.CosmeticEquips.Remove(equip);
        await ctx.SaveChangesAsync(ct);

        return await BroadcastAsync(ctx, userId, ct);
    }

    public async Task<CosmeticError?> AssignLoadoutToSpaceAsync(
        Guid? spaceId, Guid loadoutId, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        if (!await ctx.CosmeticLoadouts.AnyAsync(x => x.Id == loadoutId && x.UserId == userId, ct))
            return CosmeticError.NOT_FOUND;

        if (spaceId is { } space)
        {
            if (!await ctx.UsersToServerRelations.AnyAsync(x => x.SpaceId == space && x.UserId == userId, ct))
                return CosmeticError.NOT_FOUND;

            // A kind that is global by nature cannot be worn differently per space, so a loadout
            // holding one cannot be a space's loadout. Checked here rather than at equip time
            // because it is the assignment that makes the scope, not the equip.
            var kinds = await ctx.CosmeticEquips
               .Where(x => x.LoadoutId == loadoutId)
               .Select(x => x.KindKey)
               .Distinct()
               .ToListAsync(ct);

            foreach (var kindKey in kinds)
            {
                if (registry.TryGet(kindKey, out var kind) && !kind.SupportsScope(CosmeticScope.PerSpace))
                    return CosmeticError.SCOPE_NOT_SUPPORTED;
            }
        }

        if (spaceId is null)
        {
            await MarkDefaultAsync(ctx, userId, loadoutId, ct);
            await SetGlobalAssignmentAsync(ctx, userId, loadoutId, ct);
        }
        else
        {
            var assignment = await ctx.CosmeticScopeAssignments
               .FirstOrDefaultAsync(x => x.UserId == userId && x.SpaceId == spaceId, ct);

            if (assignment is null)
            {
                ctx.CosmeticScopeAssignments.Add(new CosmeticScopeAssignmentEntity
                {
                    Id        = ArgonId.New(),
                    UserId    = userId,
                    SpaceId   = spaceId,
                    LoadoutId = loadoutId
                });
            }
            else
            {
                assignment.LoadoutId = loadoutId;
            }
        }

        await ctx.SaveChangesAsync(ct);
        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    /// <summary>
    /// Names one look as the one worn everywhere, or none of them.
    /// </summary>
    /// <remarks>
    /// <b>The clearing is written before the setting, in a write of its own.</b> The unique index
    /// allows one flagged look per person and Postgres checks it as each row is written rather than
    /// at the end of the batch — so clearing the old flag and setting the new one together fails
    /// outright whenever the writes happen to land in that order, which is not an order anything
    /// here chooses. A moment with nothing flagged costs nothing: the flag decides nothing on its
    /// own, it is the name the global assignment goes by.
    /// </remarks>
    private static async Task MarkDefaultAsync(
        ApplicationDbContext ctx, Guid userId, Guid? loadoutId, CancellationToken ct)
    {
        var loadouts = await ctx.CosmeticLoadouts.Where(x => x.UserId == userId).ToListAsync(ct);

        foreach (var loadout in loadouts)
        {
            loadout.IsDefault = false;
        }

        await ctx.SaveChangesAsync(ct);

        if (loadoutId is not { } chosen)
            return;

        var picked = loadouts.FirstOrDefault(x => x.Id == chosen);

        if (picked is not null)
            picked.IsDefault = true;
    }

    /// <summary>
    /// What a window of people are wearing, for a member list or a page of messages.
    /// </summary>
    /// <remarks>
    /// <para><b>Capped, and the cap is the point.</b> The caller is rendering something — a visible
    /// slice of a roster, the authors on screen — and a request for ten thousand people is a mistake
    /// rather than a need. Answering the first <see cref="WornByLimit"/> makes the mistake visible
    /// in the wrong list rather than in a silo's memory.</para>
    ///
    /// <para>Everything behind it is one batch: the projection resolves the whole set in a fixed
    /// number of queries, which is what makes this worth having over the per-person read.</para>
    /// </remarks>
    public async Task<List<WornCosmetics>> GetWornByAsync(
        Guid? spaceId, List<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Membership is the anchor, the same one PrefetchProfile uses: asking what the people in a
        // space are wearing is a question only somebody in it may ask. Without this a space id would
        // be enough to read a stranger's appearance in bulk.
        if (spaceId is { } space
            && !await ctx.UsersToServerRelations.AnyAsync(x => x.SpaceId == space && x.UserId == this.GetPrimaryKey(), ct))
            return [];

        var asked = userIds.Distinct().Take(WornByLimit).ToList();

        if (asked.Count < userIds.Distinct().Count())
        {
            logger.LogWarning("Cosmetics asked for {Requested} people at once; answering the first {Limit}",
                userIds.Count, WornByLimit);
        }

        var views = await projection.BuildManyAsync(asked, spaceId, ct);
        var worn  = new List<WornCosmetics>(views.Count);

        foreach (var (userId, view) in views)
        {
            // A look that changes only the name is still an answer worth sending: wearing nothing is
            // not the same as being nobody.
            if (view.Equipped.Count == 0 && view.Identity is { DisplayName: null, AvatarFileId: null })
                continue;

            worn.Add(new WornCosmetics(
                userId,
                new IonArray<EquippedCosmetic>(view.Equipped.ToList()),
                view.Identity.DisplayName,
                view.Identity.AvatarFileId));
        }

        return worn;
    }

    /// <summary>
    /// Takes a scope back off whatever look holds it.
    /// </summary>
    /// <remarks>
    /// Removing rather than pointing somewhere else, because "nothing is assigned here" is a real
    /// state with its own meaning: nobody's look is worn in this scope. Without this a space could
    /// only ever be moved between looks, never released, and a look could never stop being the one
    /// worn everywhere.
    /// </remarks>
    public async Task<CosmeticError?> UnassignLoadoutFromSpaceAsync(Guid? spaceId, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var assignment = await ctx.CosmeticScopeAssignments
           .FirstOrDefaultAsync(x => x.UserId == userId && x.SpaceId == spaceId, ct);

        // Nothing was assigned, which is the state that was asked for.
        if (assignment is null)
            return null;

        // Releasing the global scope leaves nobody wearing a look everywhere, so nothing may still
        // be flagged as the one that does.
        if (spaceId is null)
            await MarkDefaultAsync(ctx, userId, null, ct);

        ctx.CosmeticScopeAssignments.Remove(assignment);
        await ctx.SaveChangesAsync(ct);

        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    private static async Task<List<(Guid ItemId, DateTimeOffset? ExpiresAt)>> ActiveOwnershipAsync(
        ApplicationDbContext ctx, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await ctx.CosmeticOwnerships
           .AsNoTracking()
           .Where(x => x.UserId == userId && x.RevokedAt == null)
           .Where(x => x.ExpiresAt == null || x.ExpiresAt > now)
           .Select(x => new
            {
                x.CosmeticItemId,
                x.ExpiresAt
            })
           .ToListAsync(ct);

        return rows.Select(x => (x.CosmeticItemId, x.ExpiresAt)).ToList();
    }

    /// <summary>
    /// How many cards one board may hold. A limit rather than none, because every card is read on
    /// every profile resolution.
    /// </summary>
    private const int BoardLimit = 12;

    /// <summary>
    /// The same wait the account's own display name is held to.
    /// </summary>
    /// <remarks>
    /// Per look rather than per account, because that is the thing being renamed — but present at
    /// all because a look whose name changed freely would be the account's cooldown with an extra
    /// step in front of it.
    /// </remarks>
    private static readonly TimeSpan NameCooldown = TimeSpan.FromMinutes(10);

    public async Task<CosmeticError?> SetLoadoutIdentityAsync(
        Guid loadoutId, string? displayName, string? bio, bool keepAvatar, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadout = await ctx.CosmeticLoadouts.FirstOrDefaultAsync(x => x.Id == loadoutId && x.UserId == userId, ct);

        if (loadout is null)
            return CosmeticError.NOT_FOUND;

        var name = displayName?.Trim();

        if (name is { Length: 0 })
            name = null;

        if (name is { Length: > 32 })
            return CosmeticError.NAME_INVALID;

        // Only a change costs the wait. Saving the same name again, or editing the bio beside it,
        // is not a rename.
        if (name != loadout.DisplayNameOverride)
        {
            if (loadout.DisplayNameChangedAt is { } changed && DateTimeOffset.UtcNow - changed < NameCooldown)
                return CosmeticError.NAME_INVALID;

            loadout.DisplayNameOverride  = name;
            loadout.DisplayNameChangedAt = DateTimeOffset.UtcNow;
        }

        var about = bio?.Trim();

        if (about is { Length: 0 })
            about = null;

        if (about is { Length: > 512 })
            return CosmeticError.NAME_INVALID;

        loadout.BioOverride = about;

        if (!keepAvatar)
            loadout.AvatarFileIdOverride = null;

        await ctx.SaveChangesAsync(ct);
        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    /// <summary>
    /// Finishes a look's avatar upload: the same finalise and the same moderation an account avatar
    /// goes through, landing on the look instead of on the account.
    /// </summary>
    public async Task<CosmeticError?> SetLoadoutAvatarAsync(Guid loadoutId, Guid blobId, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadout = await ctx.CosmeticLoadouts.FirstOrDefaultAsync(x => x.Id == loadoutId && x.UserId == userId, ct);

        if (loadout is null)
            return CosmeticError.NOT_FOUND;

        var files    = GrainFactory.GetGrain<IFileStorageGrain>(userId);
        var fileInfo = await files.FinalizeUploadAsync(blobId, ct);

        var verdict = await GrainFactory.GetGrain<IContentModerationGrain>(Guid.Empty)
           .EvaluateAsync(fileInfo.S3Key, FilePurpose.Avatar, ct);

        if (verdict.Action == ContentAction.Deny)
        {
            await files.DecrementRefAsync(fileInfo.FileId, ct);

            logger.LogWarning("Look avatar rejected for {User}, look {Loadout}, file {File}",
                userId, loadoutId, fileInfo.FileId);

            return CosmeticError.ITEM_UNAVAILABLE;
        }

        loadout.AvatarFileIdOverride = fileInfo.FileId.ToString();

        await ctx.SaveChangesAsync(ct);
        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    /// <summary>
    /// Takes in a picture for a board card and answers with the file it became.
    /// </summary>
    /// <remarks>
    /// <para>The same finalize and the same moderation a look's avatar gets — it is the same class of
    /// thing, a picture other people are shown — and it differs only in where the answer goes. An
    /// avatar has a column; this belongs to whichever card its wearer is editing, so the id comes
    /// back and the card carries it.</para>
    ///
    /// <para>That is also why nothing is written here. Uploading a picture and putting it on a card
    /// are separate acts: somebody may upload and then close the editor, and a half-written board is
    /// worse than none.</para>
    /// </remarks>
    public async Task<Either<string, CosmeticError>> AcceptWidgetPictureAsync(
        Guid loadoutId, Guid blobId, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        if (!await ctx.CosmeticLoadouts.AnyAsync(x => x.Id == loadoutId && x.UserId == userId, ct))
            return CosmeticError.NOT_FOUND;

        var files    = GrainFactory.GetGrain<IFileStorageGrain>(userId);
        var fileInfo = await files.FinalizeUploadAsync(blobId, ct);

        var verdict = await GrainFactory.GetGrain<IContentModerationGrain>(Guid.Empty)
           .EvaluateAsync(fileInfo.S3Key, FilePurpose.Banner, ct);

        if (verdict.Action == ContentAction.Deny)
        {
            await files.DecrementRefAsync(fileInfo.FileId, ct);

            logger.LogWarning("Card picture rejected for {User}, look {Loadout}, file {File}",
                userId, loadoutId, fileInfo.FileId);

            return CosmeticError.ITEM_UNAVAILABLE;
        }

        return fileInfo.FileId.ToString();
    }

    public async Task<CosmeticError?> SetWidgetBoardAsync(
        Guid loadoutId, List<WidgetBoardCard> cards, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        if (cards.Count > BoardLimit)
            return CosmeticError.LOADOUT_LIMIT;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var loadout = await ctx.CosmeticLoadouts.FirstOrDefaultAsync(x => x.Id == loadoutId && x.UserId == userId, ct);

        if (loadout is null)
            return CosmeticError.NOT_FOUND;

        var now        = DateTimeOffset.UtcNow;
        var owned      = await ActiveOwnershipAsync(ctx, userId, now, ct);
        var hasPremium = await ctx.Users.AnyAsync(x => x.Id == userId && x.HasActiveUltima, ct);

        var wanted = cards.Select(x => x.CosmeticId).ToList();

        var items = await ctx.Cosmetics
           .Where(x => wanted.Contains(x.Id))
           .ToDictionaryAsync(x => x.Id, ct);

        // Everything is checked before anything is written: half a board is worse than none, and a
        // refusal has to leave what was there untouched.
        foreach (var card in cards)
        {
            if (!items.TryGetValue(card.CosmeticId, out var item))
                return CosmeticError.NOT_FOUND;

            if (!registry.TryGet(item.KindKey, out var kind))
                return CosmeticError.UNKNOWN_KIND;

            if (kind.Board is null)
                return CosmeticError.ITEM_UNAVAILABLE;

            if (!CosmeticAvailability.IsServable(item, now))
                return CosmeticError.ITEM_UNAVAILABLE;

            if (!Owns(kind, item, owned, hasPremium))
                return CosmeticError.NOT_OWNED;

            var content = kind.ValidateContent(card.ContentJson);

            if (!content.IsValid)
            {
                logger.LogWarning("Refused widget content on {Kind} for {User}: {Errors}",
                    item.KindKey, userId, string.Join("; ", content.Errors));

                return CosmeticError.OVERRIDE_INVALID;
            }
        }

        var boardKinds = registry.All.Where(x => x.Board is not null).Select(x => x.Key).ToHashSet();

        var existing = await ctx.CosmeticEquips
           .Where(x => x.LoadoutId == loadoutId && boardKinds.Contains(x.KindKey))
           .ToListAsync(ct);

        ctx.CosmeticEquips.RemoveRange(existing);

        var slotPerKind = new Dictionary<string, int>();
        var perItem     = new Dictionary<Guid, int>();

        foreach (var card in cards)
        {
            var item = items[card.CosmeticId];
            var kind = registry.Find(item.KindKey)!;

            var slot = slotPerKind.GetValueOrDefault(item.KindKey);
            slotPerKind[item.KindKey] = slot + 1;

            if (slot >= kind.MaxSlots)
                return CosmeticError.SLOT_OUT_OF_RANGE;

            // The operator's own limit for this row — one of them makes the card unique. Enforced
            // here because a picker holding itself to it is a courtesy, not a guarantee.
            var held = perItem.GetValueOrDefault(card.CosmeticId) + 1;
            perItem[card.CosmeticId] = held;

            if (CosmeticBoardOffer.Resolve(kind, item) is { } policy && held > policy.MaxPerBoard)
                return CosmeticError.SLOT_OUT_OF_RANGE;

            var (x, y, w, h) = CosmeticContent.ClampCell(kind, card.X, card.Y, card.W, card.H);

            ctx.CosmeticEquips.Add(new CosmeticEquipEntity
            {
                Id             = ArgonId.New(),
                LoadoutId      = loadoutId,
                CosmeticItemId = card.CosmeticId,
                KindKey        = item.KindKey,
                SlotIndex      = slot,
                Content        = string.IsNullOrWhiteSpace(card.ContentJson) ? null : card.ContentJson,
                BoardX         = x,
                BoardY         = y,
                BoardW         = w,
                BoardH         = h
            });
        }

        await ctx.SaveChangesAsync(ct);
        await BroadcastAsync(ctx, userId, ct);

        return null;
    }

    /// <summary>
    /// Holds a wearer's axis choices to rows that exist, are switched on, and are theirs.
    /// </summary>
    /// <remarks>
    /// <para>The entitlement check is the part that cannot be left to the client. Whoever later draws
    /// this name resolves the chosen option from their own copy of the catalogue and has no way to
    /// tell that the wearer never had it, so a modified client would otherwise put a paid face on
    /// everybody's screen for free. The refusal has to happen where the row is written.</para>
    ///
    /// <para>Null means the choices are acceptable.</para>
    /// </remarks>
    private async Task<CosmeticError?> CheckChoicesAsync(
        ApplicationDbContext ctx,
        CosmeticKindDefinition kind,
        string? overridesJson,
        List<(Guid ItemId, DateTimeOffset? ExpiresAt)> owned,
        bool hasPremium,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!CosmeticChoices.TryParse(overridesJson, out var choices, out _))
            return CosmeticError.OVERRIDE_INVALID;

        foreach (var (facetId, slug) in choices)
        {
            if (!kind.Facets.TryGetValue(facetId, out var facet))
                return CosmeticError.OVERRIDE_INVALID;

            if (slug == CosmeticChoices.None)
                continue;

            if (!registry.TryGet(facet.OptionKindKey, out var optionKind))
                return CosmeticError.UNKNOWN_KIND;

            var option = await ctx.Cosmetics
               .FirstOrDefaultAsync(x => x.KindKey == facet.OptionKindKey && x.Slug == slug, ct);

            if (option is null)
                return CosmeticError.OVERRIDE_INVALID;

            if (!CosmeticAvailability.IsServable(option, now))
                return CosmeticError.ITEM_UNAVAILABLE;

            if (!Owns(optionKind, option, owned, hasPremium))
                return CosmeticError.NOT_OWNED;
        }

        return null;
    }

    private static bool Owns(
        CosmeticKindDefinition kind,
        CosmeticItemEntity item,
        List<(Guid ItemId, DateTimeOffset? ExpiresAt)> owned,
        bool hasPremium)
    {
        if (kind.Entitlement is CosmeticEntitlement.Free || item.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.Free))
            return true;

        foreach (var (itemId, _) in owned)
        {
            if (itemId == item.Id)
                return true;
        }

        return kind.Entitlement is CosmeticEntitlement.UltimaOrOwned
               && hasPremium
               && item.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.UltimaTier);
    }

    private static CosmeticKindSummary Describe(CosmeticKindDefinition kind)
    {
        var surfaces = new List<string>();

        foreach (var surface in Enum.GetValues<CosmeticSurface>())
        {
            if (surface is not CosmeticSurface.None && kind.RendersOn(surface))
                surfaces.Add(surface.ToString());
        }

        var scopes = new List<string>();

        foreach (var scope in Enum.GetValues<CosmeticScope>())
        {
            if (kind.SupportsScope(scope))
                scopes.Add(scope.ToString());
        }

        return new CosmeticKindSummary(
            kind.Key,
            kind.Primitive.ToString(),
            new IonArray<string>(surfaces),
            new IonArray<string>(scopes),
            kind.Layer,
            kind.MaxSlots);
    }

    private static CatalogueBoardPolicy? Describe(CosmeticBoardOffer? policy)
        => policy is { } board
            ? new CatalogueBoardPolicy(
                board.MaxPerBoard, board.DefaultWidth, board.DefaultHeight,
                board.MaxWidth, board.MinHeight, board.MaxHeight, board.MinWidth)
            : null;

    private static List<CosmeticAcquisition> Acquisitions(CosmeticAcquisitionMode mode)
    {
        var hints = new List<CosmeticAcquisition>();

        if (mode.HasFlag(CosmeticAcquisitionMode.OperatorGrant))
            hints.Add(CosmeticAcquisition.OperatorGrant);
        if (mode.HasFlag(CosmeticAcquisitionMode.PromoCode))
            hints.Add(CosmeticAcquisition.PromoCode);
        if (mode.HasFlag(CosmeticAcquisitionMode.UltimaTier))
            hints.Add(CosmeticAcquisition.UltimaTier);
        if (mode.HasFlag(CosmeticAcquisitionMode.Purchase))
            hints.Add(CosmeticAcquisition.Purchase);
        if (mode.HasFlag(CosmeticAcquisitionMode.Gift))
            hints.Add(CosmeticAcquisition.Gift);
        if (mode.HasFlag(CosmeticAcquisitionMode.Free))
            hints.Add(CosmeticAcquisition.Free);

        return hints;
    }

    private static IonArray<EquippedCosmeticAsset> Assets(CosmeticItemEntity item)
    {
        var assets = new List<EquippedCosmeticAsset>(item.AssetFileIds.Count);

        foreach (var (slot, fileId) in item.AssetFileIds)
        {
            assets.Add(new EquippedCosmeticAsset(slot, fileId));
        }

        return new IonArray<EquippedCosmeticAsset>(assets);
    }

    private static IonArray<CosmeticText> Text(Dictionary<Guid, List<CosmeticTranslationEntity>> byItem, Guid itemId)
    {
        if (!byItem.TryGetValue(itemId, out var rows))
            return new IonArray<CosmeticText>([]);

        return new IonArray<CosmeticText>(rows
           .Select(x => new CosmeticText(x.Locale, x.Name, x.Description))
           .ToList());
    }

    private async Task<ArgonUserProfile> ReadProfileAsync(ApplicationDbContext ctx, Guid userId, CancellationToken ct)
    {
        var profile = await ctx.UserProfiles.AsNoTracking().FirstAsync(x => x.UserId == userId, ct);

        return await projection.ApplyAsync(profile.ToDto(), null, ct);
    }

    /// <summary>
    /// Tells every space the person is in what they now look like, and hands the caller the same
    /// answer so the client does not have to ask again.
    /// </summary>
    private async Task<ArgonUserProfile> BroadcastAsync(ApplicationDbContext ctx, Guid userId, CancellationToken ct)
    {
        var globalProfile = await ReadProfileAsync(ctx, userId, ct);

        var spaceIds = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.UserId == userId)
           .Select(x => x.SpaceId)
           .ToListAsync(ct);

        foreach (var spaceId in spaceIds)
        {
            // Resolved per space, because a person can wear a different loadout in each and a
            // broadcast carrying the global answer would show everyone the wrong one.
            var profile = await ctx.UserProfiles.AsNoTracking().FirstAsync(x => x.UserId == userId, ct);
            var scoped  = await projection.ApplyAsync(profile.ToDto(), spaceId, ct);

            await appHubServer.BroadcastSpace(new UserProfileUpdated(spaceId, userId, scoped), spaceId, ct);
        }

        return globalProfile;
    }
}
