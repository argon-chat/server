namespace Argon.Api.Features.AdminApi;

using System.Collections.Frozen;
using Argon.Api.Entities.Data;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Admin;
using Argon.Features.Cosmetics;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ConsoleContracts;
using ion.runtime;

/// <summary>
/// The operator surface for profile cosmetics: what the build ships, what is in the catalogue, and
/// who holds what.
/// </summary>
/// <remarks>
/// <para>Split from <c>AdminConsoleImpl</c> rather than appended to it because that file is already
/// past three and a half thousand lines and this is a self-contained subject.</para>
///
/// <para><b>Nothing here can create or destroy a kind.</b> A kind exists because a file in the build
/// declares it; the console reports what the registry found, switches a kind off through its feature
/// flag, and manages the catalogue rows underneath. That division is the whole point of the design
/// and it is worth not eroding: a console that could invent a kind would be inventing one with no
/// renderer on the other side.</para>
/// </remarks>
public partial class AdminConsoleImpl
{
    public async Task<CosmeticKindList> GetCosmeticKinds(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var flagGrain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var flags     = await flagGrain.ListFlagsAsync();

        var flagsById = new Dictionary<string, FeatureFlagSummaryDto>(StringComparer.Ordinal);

        foreach (var flag in flags)
        {
            flagsById[flag.Id] = flag;
        }

        var counts = await db.Cosmetics
           .GroupBy(x => x.KindKey)
           .Select(g => new
            {
                KindKey   = g.Key,
                Total     = g.Count(),
                Published = g.Count(x => x.IsPublished)
            })
           .ToListAsync(ct);

        var countsByKind = new Dictionary<string, (int Total, int Published)>(StringComparer.Ordinal);

        foreach (var row in counts)
        {
            countsByKind[row.KindKey] = (row.Total, row.Published);
        }

        var kinds = new List<CosmeticKindInfo>(cosmeticKinds.All.Count);

        foreach (var definition in cosmeticKinds.All)
        {
            var hasFlag = flagsById.TryGetValue(definition.FeatureFlagKey, out var flag);
            var count   = countsByKind.GetValueOrDefault(definition.Key);

            kinds.Add(MapKind(definition,
                CosmeticKindGate.IsEnabled(hasFlag, flag?.DefaultEnabled ?? true),
                count.Total,
                count.Published));
        }

        // Keys the catalogue still carries that no file declares any more. Counted from both tables:
        // an operator deciding whether to purge wants to know how many people are wearing one, not
        // only how many rows exist.
        var orphans = new List<CosmeticOrphanInfo>();

        foreach (var row in counts)
        {
            if (cosmeticKinds.Contains(row.KindKey))
                continue;

            var equipped = await db.CosmeticEquips.CountAsync(x => x.KindKey == row.KindKey, ct);

            orphans.Add(new CosmeticOrphanInfo(row.KindKey, row.Total, equipped));
        }

        return new CosmeticKindList(new IonArray<CosmeticKindInfo>(kinds), new IonArray<CosmeticOrphanInfo>(orphans));
    }

    public async Task<UserActionResult> SetCosmeticKindEnabled(string kindKey, bool isEnabled, CancellationToken ct = default)
    {
        try
        {
            if (!cosmeticKinds.TryGet(kindKey, out var definition))
                return new UserActionResult(false, $"No cosmetic kind '{kindKey}' in this build");

            var flagGrain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
            var existing  = await flagGrain.GetFlagAsync(definition.FeatureFlagKey);

            var input = new FeatureFlagInput(
                definition.FeatureFlagKey,
                existing?.Description ?? $"Cosmetic kind '{definition.Key}' — created by the console when the kind was first switched",
                isEnabled,
                existing?.RolloutPercentage,
                existing?.Variants,
                existing?.UssdActivationCode,
                existing?.ExpiresAt);

            var result = existing is null
                ? await flagGrain.CreateFlagAsync(input)
                : await flagGrain.UpdateFlagAsync(input);

            if (!result.Success)
                return new UserActionResult(false, result.Error);

            cosmeticProjection.InvalidateGates();

            await auditService.LogAsync("SetCosmeticKindEnabled", "CosmeticKind", kindKey,
                $"flag={definition.FeatureFlagKey}, enabled={isEnabled}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> PurgeOrphanedCosmetics(string kindKey, CancellationToken ct = default)
    {
        try
        {
            // Refusing to purge a kind that still exists is the safety the whole orphan design rests
            // on. Without it this method is "delete every cosmetic of a kind", one typo away from
            // undressing everybody wearing a live one.
            if (cosmeticKinds.Contains(kindKey))
                return new UserActionResult(false, $"Kind '{kindKey}' is declared in this build and is not an orphan");

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var items = await db.Cosmetics.Where(x => x.KindKey == kindKey).ToListAsync(ct);

            if (items.Count == 0)
                return new UserActionResult(false, $"Nothing stored under '{kindKey}'");

            // The kind's file being gone does not mean the bytes are. A shipped row's files are
            // inside applications people are running, and this is the one route left that would
            // delete such a row — along with everyone's ownership of it — without ever saying so.
            var shipped = items.Where(x => x.ShippedInClientAt is not null).Select(x => x.Slug).ToList();

            if (shipped.Count > 0)
                return new UserActionResult(false,
                    $"{shipped.Count} row(s) under '{kindKey}' shipped in a client build ({string.Join(", ", shipped.Take(3))}"
                    + $"{(shipped.Count > 3 ? ", …" : "")}). Unmark them first.");

            var itemIds = items.Select(x => x.Id).ToList();

            var equips = await db.CosmeticEquips
               .Where(x => x.CosmeticItemId != null && itemIds.Contains(x.CosmeticItemId.Value))
               .ToListAsync(ct);

            var ownerships = await db.CosmeticOwnerships.Where(x => itemIds.Contains(x.CosmeticItemId)).ToListAsync(ct);

            db.CosmeticEquips.RemoveRange(equips);
            db.CosmeticOwnerships.RemoveRange(ownerships);
            db.Cosmetics.RemoveRange(items);

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("PurgeOrphanedCosmetics", "CosmeticKind", kindKey,
                $"items={items.Count}, equips={equips.Count}, ownerships={ownerships.Count}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<CosmeticPage> SearchCosmetics(CosmeticQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var limit  = Math.Clamp(query.limit <= 0 ? 50 : query.limit, 1, 200);
        var offset = Math.Max(query.offset, 0);

        var rows = db.Cosmetics.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.kindKey))
            rows = rows.Where(x => x.KindKey == query.kindKey);

        if (query.onlyPublished)
            rows = rows.Where(x => x.IsPublished);

        if (query.onlyEnabled)
            rows = rows.Where(x => x.IsEnabled);

        if (query.onlyShipped)
            rows = rows.Where(x => x.ShippedInClientAt != null);

        if (!string.IsNullOrWhiteSpace(query.text))
        {
            var text = query.text.Trim();

            rows = rows.Where(x => EF.Functions.ILike(x.Slug, $"%{text}%")
                                || EF.Functions.ILike(x.NameKey, $"%{text}%")
                                || db.CosmeticTranslations.Any(t => t.CosmeticItemId == x.Id && EF.Functions.ILike(t.Name, $"%{text}%")));
        }

        var total = await rows.CountAsync(ct);

        var page = await rows
           .OrderBy(x => x.KindKey)
           .ThenBy(x => x.SortOrder)
           .ThenBy(x => x.Slug)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);

        var pageIds = page.Select(x => x.Id).ToList();

        var names = await db.CosmeticTranslations
           .AsNoTracking()
           .Where(x => pageIds.Contains(x.CosmeticItemId) && x.Locale == CosmeticLocale.Fallback)
           .ToDictionaryAsync(x => x.CosmeticItemId, x => x.Name, ct);

        var items = new List<CosmeticSummary>(page.Count);

        foreach (var item in page)
        {
            items.Add(MapSummary(item, names.GetValueOrDefault(item.Id)));
        }

        return new CosmeticPage(new IonArray<CosmeticSummary>(items), total, offset, limit);
    }

    public async Task<CosmeticDetails> GetCosmetic(Guid cosmeticId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct)
                   ?? throw new KeyNotFoundException($"Cosmetic {cosmeticId} not found");

        var definition = cosmeticKinds.Find(item.KindKey);
        var payload    = definition?.ValidatePayload(item.Payload);

        var ownerCount = await db.CosmeticOwnerships
           .CountAsync(x => x.CosmeticItemId == cosmeticId && x.RevokedAt == null, ct);

        var equippedCount = await db.CosmeticEquips.CountAsync(x => x.CosmeticItemId == cosmeticId, ct);

        CosmeticKindInfo? kindInfo = null;

        if (definition is not null)
        {
            var flagGrain = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);
            var flag      = await flagGrain.GetFlagAsync(definition.FeatureFlagKey);
            var total     = await db.Cosmetics.CountAsync(x => x.KindKey == item.KindKey, ct);
            var published = await db.Cosmetics.CountAsync(x => x.KindKey == item.KindKey && x.IsPublished, ct);

            kindInfo = MapKind(definition,
                CosmeticKindGate.IsEnabled(flag is not null, flag?.DefaultEnabled ?? true),
                total,
                published);
        }

        var assets = new List<CosmeticAssetInfo>(item.AssetFileIds.Count);

        foreach (var (slot, fileId) in item.AssetFileIds)
        {
            if (Enum.TryParse<CosmeticAssetSlot>(slot, out var parsed))
                assets.Add(new CosmeticAssetInfo(ToWireSlot(parsed), fileId));
        }

        var translations = await db.CosmeticTranslations
           .AsNoTracking()
           .Where(x => x.CosmeticItemId == item.Id)
           .OrderBy(x => x.Locale)
           .Select(x => new CosmeticTranslationInfo(x.Locale, x.Name, x.Description))
           .ToListAsync(ct);

        return new CosmeticDetails(
            item.Id,
            item.KindKey,
            kindInfo,
            item.Slug,
            item.NameKey,
            item.DescriptionKey,
            item.Rarity,
            item.SortOrder,
            item.Version,
            item.Payload,
            payload?.IsValid ?? false,
            new IonArray<string>(payload?.Errors.ToList() ?? ["the kind that owns this row is not in this build"]),
            // The engine's resolver and the wire message share a name; the wire one wins in this file.
            definition is not null && CosmeticBoardOffer.Resolve(definition, item) is { } board
                ? new CosmeticBoardPolicy(board.MaxPerBoard, board.DefaultWidth, board.DefaultHeight)
                : null,
            item.AssetSource is CosmeticAssetSource.UserProvided
                ? CosmeticAssetSourceKind.UserProvided
                : CosmeticAssetSourceKind.Catalogue,
            new IonArray<CosmeticAssetInfo>(assets),
            item.IsEnabled,
            item.IsPublished,
            item.PublishedAt,
            item.AvailableFrom,
            item.AvailableUntil,
            new IonArray<CosmeticAcquisitionKind>(ToWireAcquisition(item.AcquisitionMode)),
            item.UltimaTierRequired is { } tier ? ToWirePlan(tier) : null,
            item.PriceSku,
            item.GrantItemTemplateId,
            item.LegacyId,
            ownerCount,
            equippedCount,
            item.CreatedAt,
            item.ShippedInClientAt,
            item.ShippedInClientBuild,
            new IonArray<CosmeticTranslationInfo>(translations));
    }

    /// <summary>
    /// The rarities the client can actually draw. A key item's rarity is not decoration — it is what
    /// the loot-grant animation on the client reads to choose a colour (<c>ItemQuality</c> in
    /// <c>client/packages/inventory/src/index.ts</c>), and a value outside this set is not a
    /// validation nicety: it is a cosmetic that renders grey, silently, with nothing anywhere else in
    /// the chain ever raising an error about it.
    /// </summary>
    /// <remarks>
    /// Declared once and checked from both <see cref="CreateCosmetic"/> and <see cref="UpdateCosmetic"/>
    /// through <see cref="TryNormalizeRarity"/> rather than duplicated in each — a second copy of this
    /// list is exactly the kind of drift the comments elsewhere in this file keep warning about.
    /// </remarks>
    private static readonly FrozenSet<string> ValidRarities =
        new[] { "common", "rare", "legendary", "relic" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Validates a rarity against <see cref="ValidRarities"/> and returns the exact form to store.
    /// </summary>
    /// <remarks>
    /// Null or blank passes through as null — not every cosmetic has a rarity, and that stays legal.
    /// A value that is present is lowercased before the check, and it is the lowercased value, never
    /// the operator's own casing, that is returned to store: an operator who types "Legendary" meant
    /// "legendary". Storing the canonical form here — rather than storing what was typed and trusting
    /// the client to cope — is what keeps the client's case-insensitive comparison a hedge that is
    /// never actually load-bearing. Only a value that is not in the set at all is refused.
    /// </remarks>
    private static bool TryNormalizeRarity(string? rarity, out string? normalized, out string? error)
    {
        if (string.IsNullOrWhiteSpace(rarity))
        {
            normalized = null;
            error      = null;
            return true;
        }

        var lowered = rarity.Trim().ToLowerInvariant();

        if (!ValidRarities.Contains(lowered))
        {
            normalized = null;
            error = $"'{rarity}' is not a rarity the client can draw; use one of: {string.Join(", ", ValidRarities)}";
            return false;
        }

        normalized = lowered;
        error      = null;
        return true;
    }

    public async Task<CreateCosmeticResult> CreateCosmetic(CreateCosmeticInput input, CancellationToken ct = default)
    {
        try
        {
            if (!cosmeticKinds.TryGet(input.kindKey, out var definition))
                return new CreateCosmeticResult(false, null, $"No cosmetic kind '{input.kindKey}' in this build");

            // A bare kind is configured rather than chosen: it has no rows by definition, and a row
            // for one would be an offer nobody could accept.
            if (definition.IsBare)
            {
                return new CreateCosmeticResult(false, null,
                    $"'{input.kindKey}' is configured rather than chosen and has no catalogue rows");
            }

            if (string.IsNullOrWhiteSpace(input.slug))
                return new CreateCosmeticResult(false, null, "A slug is required");

            if (!IsUsableSlug(input.slug.Trim()))
                return new CreateCosmeticResult(false, null,
                    "A slug is lowercase letters, digits, - and _, between 2 and 128 characters");

            var payload = definition.ValidatePayload(input.payloadJson);

            if (!payload.IsValid)
                return new CreateCosmeticResult(false, null, string.Join("; ", payload.Errors));

            if (!TryNormalizeRarity(input.rarity, out var rarity, out var rarityError))
                return new CreateCosmeticResult(false, null, rarityError);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var slug = input.slug.Trim();

            if (await db.Cosmetics.AnyAsync(x => x.KindKey == input.kindKey && x.Slug == slug, ct))
                return new CreateCosmeticResult(false, null, $"'{slug}' already exists for kind '{input.kindKey}'");

            // A key is no longer a display name, so an operator has no reason to invent one. It is
            // still served and still what the client falls back to, so it has to be something
            // stable — the slug it already chose, under the kind it already picked.
            //
            // Truncated to the column, because a kind key and a slug may together outrun it and
            // nothing depends on this value being whole: it is not unique, nothing outside reads it,
            // and a row that reaches a client without a name is refused publication anyway. Losing
            // the tail beats an EF exception where a written refusal belongs.
            var nameKey = string.IsNullOrWhiteSpace(input.nameKey)
                ? Truncate($"cosmetic_{input.kindKey.Replace('.', '_').Replace('-', '_')}_{slug.Replace('-', '_')}", NameKeyLimit)
                : input.nameKey.Trim();

            var item = new CosmeticItemEntity
            {
                Id             = ArgonId.New(),
                KindKey        = input.kindKey,
                Slug           = slug,
                NameKey        = nameKey,
                DescriptionKey = input.descriptionKey,
                Rarity         = rarity,
                SortOrder      = input.sortOrder,
                Payload        = input.payloadJson,
                AssetSource = input.assetSource is CosmeticAssetSourceKind.UserProvided
                    ? CosmeticAssetSource.UserProvided
                    : CosmeticAssetSource.Catalogue,

                // Created switched on but unpublished: enabled is the per-item kill switch and it
                // would be strange to start it in the killed state, while published is the gate and
                // has to be earned.
                IsEnabled   = true,
                IsPublished = false
            };

            db.Cosmetics.Add(item);

            if (!string.IsNullOrWhiteSpace(input.name))
            {
                db.CosmeticTranslations.Add(new CosmeticTranslationEntity
                {
                    Id             = Guid.NewGuid(),
                    CosmeticItemId = item.Id,
                    Locale         = CosmeticLocale.Fallback,
                    Name           = input.name.Trim(),
                    Description    = string.IsNullOrWhiteSpace(input.description) ? null : input.description.Trim()
                });
            }

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("CreateCosmetic", "Cosmetic", item.Id.ToString(),
                $"kind={input.kindKey}, slug={slug}");

            return new CreateCosmeticResult(true, item.Id, null);
        }
        catch (Exception ex)
        {
            return new CreateCosmeticResult(false, null, ex.Message);
        }
    }

    public async Task<UserActionResult> UpdateCosmetic(UpdateCosmeticInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == input.cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            if (input.payloadJson is not null)
            {
                if (!cosmeticKinds.TryGet(item.KindKey, out var definition))
                    return new UserActionResult(false, $"No cosmetic kind '{item.KindKey}' in this build");

                var payload = definition.ValidatePayload(input.payloadJson);

                if (!payload.IsValid)
                    return new UserActionResult(false, string.Join("; ", payload.Errors));

                item.Payload = input.payloadJson;

                // Bumped so a client can tell a re-authored cosmetic from the one it cached. Only the
                // payload moves it: a rename changes no pixels.
                item.Version += 1;
            }

            if (input.nameKey is not null)
                item.NameKey = input.nameKey;

            if (input.descriptionKey is not null)
                item.DescriptionKey = string.IsNullOrWhiteSpace(input.descriptionKey) ? null : input.descriptionKey;

            if (input.rarity is not null)
            {
                if (!TryNormalizeRarity(input.rarity, out var rarity, out var rarityError))
                    return new UserActionResult(false, rarityError);

                item.Rarity = rarity;
            }

            if (input.sortOrder is { } sortOrder)
                item.SortOrder = sortOrder;

            // Setting a window on a shipped row is refused; clearing one is not. "No longer
            // temporary" is the whole of what the flag promises, and removing a window is the
            // direction that keeps that promise.
            var windowWanted = (!input.clearAvailableFrom && input.availableFrom is not null)
                            || (!input.clearAvailableUntil && input.availableUntil is not null);

            if (windowWanted && ShippedRefusal(item, "Giving it an availability window") is { } refusal)
                return new UserActionResult(false, refusal);

            if (input.clearAvailableFrom)
                item.AvailableFrom = null;
            else if (input.availableFrom is { } from)
                item.AvailableFrom = from;

            if (input.clearAvailableUntil)
                item.AvailableUntil = null;
            else if (input.availableUntil is { } until)
                item.AvailableUntil = until;

            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("UpdateCosmetic", "Cosmetic", item.Id.ToString());

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// What an operator decides about one card on the profile board.
    /// </summary>
    /// <remarks>
    /// <para>Refused rather than clamped when an input is outside what the kind can draw. Clamping
    /// would let somebody set six of a card the code draws four of, see it saved, and find out later
    /// that it was silently two — a decision an operator made should either hold or be argued with.</para>
    ///
    /// <para>The capability comes from the kind's file and cannot be raised here: what a card can do
    /// is its component's business, and this is only what is offered of it.</para>
    /// </remarks>
    public async Task<UserActionResult> SetCosmeticBoardPolicy(CosmeticBoardPolicyInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == input.cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            if (!cosmeticKinds.TryGet(item.KindKey, out var kind))
                return new UserActionResult(false, $"This build has no kind '{item.KindKey}'");

            if (kind.Board is not { } board)
                return new UserActionResult(false, $"'{item.KindKey}' draws no card on the board, so there is nothing to decide here");

            if (input.maxPerBoard is { } perBoard && (perBoard < 1 || perBoard > kind.MaxSlots))
                return new UserActionResult(false, $"A board holds between one and {kind.MaxSlots} of this card");

            if (input.defaultWidth is { } width && (width < board.MinWidth || width > board.MaxWidth))
                return new UserActionResult(false, $"This card is between {board.MinWidth} and {board.MaxWidth} columns wide");

            if (input.defaultHeight is { } height && (height < board.MinHeight || height > board.MaxHeight))
                return new UserActionResult(false, $"This card is between {board.MinHeight} and {board.MaxHeight} rows tall");

            item.MaxPerBoard   = input.maxPerBoard;
            item.BoardDefaultW = input.defaultWidth;
            item.BoardDefaultH = input.defaultHeight;

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("SetCosmeticBoardPolicy", "Cosmetic", item.Id.ToString(),
                $"maxPerBoard={item.MaxPerBoard?.ToString() ?? "kind"}, size={item.BoardDefaultW?.ToString() ?? "kind"}x{item.BoardDefaultH?.ToString() ?? "kind"}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> SetCosmeticAcquisition(CosmeticAcquisitionInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == input.cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            // A wire list can only speak about what its enum declares, so a flag it has no member for
            // arrives as absent rather than as cleared — and assigning the whole mode would read that
            // silence as "take it away". Free is exactly that flag today, and it is not a label: it is
            // what CosmeticsGrain.Owns and CosmeticProfileProjection check before ownership, so losing
            // it stops the thing rendering for everybody already wearing it. Every caller of this is
            // rebuilding the set from what it could represent — the console's key dialog does it on
            // every "Make the key" — so the fix belongs here rather than in any one of them: only the
            // representable bits are replaced, and the rest of the stored mode survives the round trip.
            var mode = (item.AcquisitionMode & ~WireAcquisitionFlags) | FromWireAcquisition(input.acquisition);

            if (mode is CosmeticAcquisitionMode.None)
                return new UserActionResult(false, "A cosmetic nobody can acquire is not a cosmetic");

            if (mode.HasFlag(CosmeticAcquisitionMode.PromoCode) && string.IsNullOrWhiteSpace(input.grantItemTemplateId))
                return new UserActionResult(false, "A promo-code cosmetic needs the inventory template a redemption mints");

            // GrantItemTemplateId is free text, not a foreign key, so the console would otherwise
            // show an operator's typo back as if it were wired up. Checked against the same table
            // CreateItemTemplate and GetItemTemplates treat as "the templates": IsReference rows.
            if (!string.IsNullOrWhiteSpace(input.grantItemTemplateId))
            {
                var templateExists = await db.Items.AnyAsync(i => i.IsReference && i.TemplateId == input.grantItemTemplateId, ct);

                if (!templateExists)
                    return new UserActionResult(false, $"No item template '{input.grantItemTemplateId}' exists");
            }

            item.AcquisitionMode     = mode;
            item.UltimaTierRequired  = input.ultimaTierRequired is { } plan ? FromWirePlan(plan) : null;
            item.PriceSku            = string.IsNullOrWhiteSpace(input.priceSku) ? null : input.priceSku;
            item.GrantItemTemplateId = string.IsNullOrWhiteSpace(input.grantItemTemplateId) ? null : input.grantItemTemplateId;

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("SetCosmeticAcquisition", "Cosmetic", item.Id.ToString(),
                $"mode={mode}, tier={item.UltimaTierRequired}, sku={item.PriceSku}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> SetCosmeticEnabled(Guid cosmeticId, bool isEnabled, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            // Only switching it off: a shipped row being switched back on is a row returning to the
            // state its own files assume.
            if (!isEnabled && ShippedRefusal(item, "Switching it off") is { } refusal)
                return new UserActionResult(false, refusal);

            item.IsEnabled = isEnabled;
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("SetCosmeticEnabled", "Cosmetic", cosmeticId.ToString(), $"enabled={isEnabled}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// What the row is called, in one language.
    /// </summary>
    /// <remarks>
    /// An upsert rather than an add and an edit, because (cosmetic, locale) is the key and the
    /// console has no id to hold on to — a second write of the same language is a correction, which
    /// is the common case, not a duplicate.
    /// </remarks>
    public async Task<UserActionResult> SetCosmeticTranslation(CosmeticTranslationInput input, CancellationToken ct = default)
    {
        try
        {
            if (!CosmeticLocale.TryNormalize(input.locale, out var locale, out var localeError))
                return new UserActionResult(false, localeError);

            if (string.IsNullOrWhiteSpace(input.name))
                return new UserActionResult(false, "A name is required");

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == input.cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            var row = await db.CosmeticTranslations
               .FirstOrDefaultAsync(x => x.CosmeticItemId == item.Id && x.Locale == locale, ct);

            if (row is null)
            {
                row = new CosmeticTranslationEntity
                {
                    Id             = Guid.NewGuid(),
                    CosmeticItemId = item.Id,
                    Locale         = locale,
                    Name           = input.name.Trim()
                };

                db.CosmeticTranslations.Add(row);
            }
            else
            {
                row.Name = input.name.Trim();
            }

            row.Description = string.IsNullOrWhiteSpace(input.description) ? null : input.description.Trim();

            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("SetCosmeticTranslation", "Cosmetic", item.Id.ToString(), $"locale={locale}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Takes one language's name away.
    /// </summary>
    public async Task<UserActionResult> DeleteCosmeticTranslation(Guid cosmeticId, string locale, CancellationToken ct = default)
    {
        try
        {
            if (!CosmeticLocale.TryNormalize(locale, out var normalized, out var localeError))
                return new UserActionResult(false, localeError);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            // Every client falls back to this one, so removing it from something already on screen
            // would make it nameless everywhere at once. Unpublish first if that is really wanted.
            if (item.IsPublished && normalized == CosmeticLocale.Fallback)
                return new UserActionResult(false, $"'{CosmeticLocale.Fallback}' is what every client falls back to and this row is published");

            var row = await db.CosmeticTranslations
               .FirstOrDefaultAsync(x => x.CosmeticItemId == item.Id && x.Locale == normalized, ct);

            if (row is null)
                return new UserActionResult(false, $"Nothing written in '{normalized}'");

            db.CosmeticTranslations.Remove(row);

            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("DeleteCosmeticTranslation", "Cosmetic", item.Id.ToString(), $"locale={normalized}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<PublishCosmeticResult> PublishCosmetic(Guid cosmeticId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null)
                return new PublishCosmeticResult(false, PublishCosmeticError.NotFound, null);

            var namedLocales = await db.CosmeticTranslations
               .AsNoTracking()
               .Where(x => x.CosmeticItemId == item.Id)
               .Select(x => x.Locale)
               .ToListAsync(ct);

            var verdict = CosmeticPublication.Evaluate(item, cosmeticKinds.Find(item.KindKey),
                namedLocales.ToHashSet(StringComparer.Ordinal));

            if (!verdict.IsAllowed)
            {
                await auditService.LogAsync("PublishCosmeticRefused", "Cosmetic", cosmeticId.ToString(),
                    $"{verdict.Refusal}: {verdict.Detail}");

                return new PublishCosmeticResult(false, ToWireRefusal(verdict.Refusal), verdict.Detail);
            }

            item.IsPublished = true;
            item.PublishedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("PublishCosmetic", "Cosmetic", cosmeticId.ToString(),
                $"kind={item.KindKey}, slug={item.Slug}");

            return new PublishCosmeticResult(true, PublishCosmeticError.None, null);
        }
        catch (Exception ex)
        {
            return new PublishCosmeticResult(false, PublishCosmeticError.None, ex.Message);
        }
    }

    public async Task<UserActionResult> UnpublishCosmetic(Guid cosmeticId, string reason, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            if (ShippedRefusal(item, "Unpublishing it") is { } refusal)
                return new UserActionResult(false, refusal);

            item.IsPublished = false;
            await db.SaveChangesAsync(ct);

            // Deliberately not refused when a key template still points at this row, even though
            // unpublishing is the cheaper way to reach the broken state DeleteCosmetic refuses to
            // create. Unpublishing is the emergency brake — it is what an operator reaches for the
            // moment they realise something about a live row is wrong — and a refusal would put
            // dismantling the drop in front of stopping the damage. It would not make anybody safe
            // either: the keys already minted are scattered across inventories and no console call can
            // recall them. So the safety lives at the other end, where it now is — spending a key on a
            // row that is not servable grants nothing and leaves the item in the inventory — and this
            // only warns, beside the reason, because the next person to ask why the drop stopped
            // working reads exactly that.
            var keyTemplates = await KeyTemplateIdsForAsync(db, cosmeticId, ct);

            if (keyTemplates.Count > 0)
            {
                logger.LogWarning("Unpublished cosmetic {CosmeticId} still has {Count} key template(s) pointing at it: {Templates}",
                    cosmeticId, keyTemplates.Count, string.Join(", ", keyTemplates));
            }

            // The reason is the whole value of this call over SetCosmeticEnabled: unpublishing is
            // what happens when something about a row turns out to be wrong, and the next person to
            // ask why reads the audit log.
            await auditService.LogAsync("UnpublishCosmetic", "Cosmetic", cosmeticId.ToString(),
                keyTemplates.Count > 0
                    ? $"{reason} — keys for it are still being minted by: {string.Join(", ", keyTemplates)}"
                    : reason);

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<DeleteItemResult> DeleteCosmetic(Guid cosmeticId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null)
                return new DeleteItemResult(false, null, "Cosmetic not found");

            // Ahead of the publication check so the answer names the real obstacle: a shipped row is
            // published by definition, and "unpublish it first" would send an operator at a door
            // that is also locked.
            if (ShippedRefusal(item, "Deleting it") is { } refusal)
                return new DeleteItemResult(false, cosmeticId, refusal);

            if (item.IsPublished)
                return new DeleteItemResult(false, cosmeticId, "Unpublish it first");

            // Deleting something people hold or wear is not a catalogue edit, it is taking property
            // off accounts. Unpublishing already stops it being acquired, so there is no case where
            // this refusal blocks the actual need.
            var equipped = await db.CosmeticEquips.CountAsync(x => x.CosmeticItemId == cosmeticId, ct);
            var owned    = await db.CosmeticOwnerships.CountAsync(x => x.CosmeticItemId == cosmeticId && x.RevokedAt == null, ct);

            if (equipped > 0 || owned > 0)
                return new DeleteItemResult(false, cosmeticId, $"{owned} account(s) hold it and {equipped} wear it");

            // CosmeticScenario.CosmeticId carries no foreign key back to this table — deliberately,
            // so the inventory's scenario table does not answer to the cosmetics catalogue's schema
            // — so a live key template is the one thing standing in for that constraint. Checked
            // against template rows only, not keys already granted: a granted key has its own copy
            // of the scenario, already fails closed on use (see
            // A_key_to_a_cosmetic_that_is_gone_is_refused_and_keeps_the_item), and there could be
            // any number of them scattered across accounts with nothing an operator could do about
            // it here. A template is a small, operator-owned set with an actual remedy: remove it.
            var keyTemplates = await KeyTemplateIdsForAsync(db, cosmeticId, ct);

            if (keyTemplates.Count > 0)
                return new DeleteItemResult(false, cosmeticId, $"Item template '{keyTemplates[0]}' is a key for this cosmetic");

            db.Cosmetics.Remove(item);
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("DeleteCosmetic", "Cosmetic", cosmeticId.ToString(),
                $"kind={item.KindKey}, slug={item.Slug}");

            return new DeleteItemResult(true, cosmeticId, null);
        }
        catch (Exception ex)
        {
            return new DeleteItemResult(false, cosmeticId, ex.Message);
        }
    }

    /// <summary>
    /// Record that this row's files went into a client build — or take that record back.
    /// </summary>
    /// <remarks>
    /// <para><b>It changes nothing about what the api serves.</b> The same file ids go over the wire
    /// afterwards, because the server has no idea which build anybody is running: a client that has
    /// the bytes draws them from its bundle, and one that does not fetches them, and neither needs
    /// telling. What this changes is the row — from here on it cannot be deleted, switched off,
    /// unpublished or made temporary, because none of those can reach files that are already inside
    /// somebody's application.</para>
    ///
    /// <para>Only a published row can be marked, because only a published row has files anybody was
    /// ever served. A row with an availability window is refused rather than silently stripped of
    /// it: a window on something permanent is a contradiction, and deleting the dates on the
    /// operator's behalf would destroy the only record of what they were.</para>
    ///
    /// <para>Unmarking is allowed and deliberately not hedged. The flag is typed in by hand after a
    /// release, which is exactly the kind of thing that gets done to the wrong row, and a mistake
    /// nobody can undo is worse than one they can.</para>
    /// </remarks>
    public async Task<UserActionResult> SetCosmeticShipped(
        Guid cosmeticId, string build, bool shipped, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            if (shipped)
            {
                if (!item.IsPublished)
                    return new UserActionResult(false, "Publish it first — an unpublished row has no files anybody was served");

                var named = build.Trim();

                if (string.IsNullOrEmpty(named))
                    return new UserActionResult(false, "Name the client build it shipped in");

                if (named.Length > 64)
                    return new UserActionResult(false, "The build name is longer than 64 characters");

                // Refused rather than cleared. A window on something permanent is a contradiction,
                // but silently deleting the dates would destroy the only record of them — unmarking
                // could not put them back, and the operator who marked the wrong row would have no
                // way to find out what the window had been.
                if (item.AvailableFrom is not null || item.AvailableUntil is not null)
                    return new UserActionResult(false,
                        "Clear the availability window first — a shipped cosmetic cannot be temporary, "
                        + "and this will not throw the dates away on your behalf");

                item.ShippedInClientAt    = DateTimeOffset.UtcNow;
                item.ShippedInClientBuild = named;
            }
            else
            {
                item.ShippedInClientAt    = null;
                item.ShippedInClientBuild = null;
            }

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("SetCosmeticShipped", "Cosmetic", cosmeticId.ToString(),
                shipped ? $"shipped in {item.ShippedInClientBuild}" : "no longer marked as shipped");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    /// <summary>The glyphs a code is made of: no I, O, 0 or 1, because codes get read aloud and retyped.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int CodeBodyLength = 10;

    /// <summary>
    /// A batch of single-use codes for one cosmetic.
    /// </summary>
    /// <remarks>
    /// <para>Built on the coupon machinery rather than beside it: a code mints the key item into the
    /// person's inventory, and using the key is what grants the cosmetic. Two steps on purpose — the
    /// key is a real object that can be looked at, kept, or given away, and a spare one is worth
    /// holding because a duplicate is refused rather than eaten.</para>
    ///
    /// <para>The key template has to exist already. Making one from here would mean this call could
    /// mint a template, a coupon batch and an acquisition mode in one press, and an operator who
    /// mistyped the cosmetic would have three things to unpick instead of one.</para>
    ///
    /// <para>The codes are returned once. Nothing stores them in a form this screen could show
    /// again, which is why the console downloads them immediately.</para>
    /// </remarks>
    public async Task<CreateCosmeticCodesResult> CreateCosmeticCodes(
        CreateCosmeticCodesInput input, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == input.cosmeticId, ct);

            if (item is null)
                return new CreateCosmeticCodesResult(false, "Cosmetic not found", new IonArray<string>([]));

            if (input.count is < 1 or > 1000)
                return new CreateCosmeticCodesResult(false, "Between 1 and 1000 codes at a time", new IonArray<string>([]));

            if (input.validTo <= input.validFrom)
                return new CreateCosmeticCodesResult(false, "The window ends before it starts", new IonArray<string>([]));

            var template = await db.Items
               .Include(x => x.Scenario)
               .FirstOrDefaultAsync(x => x.IsReference && x.TemplateId == input.referenceItemTemplateId, ct);

            if (template is null)
                return new CreateCosmeticCodesResult(false,
                    $"No item template '{input.referenceItemTemplateId}'", new IonArray<string>([]));

            // A code that hands out somebody else's key is a code nobody can explain afterwards, so
            // the template has to be a key for this row and not merely a key.
            if (template.Scenario is not CosmeticScenario scenario)
                return new CreateCosmeticCodesResult(false,
                    $"'{input.referenceItemTemplateId}' is not a key — make one with \"Make a key\" first",
                    new IonArray<string>([]));

            if (scenario.CosmeticId != input.cosmeticId)
                return new CreateCosmeticCodesResult(false,
                    $"'{input.referenceItemTemplateId}' is a key for a different cosmetic", new IonArray<string>([]));

            var prefix = NormalizeCodePrefix(input.prefix, item.Slug);
            var codes  = new List<string>(input.count);
            var drawn  = new HashSet<string>(StringComparer.Ordinal);

            // Collisions are vanishingly unlikely and checked anyway, because `Code` is uniquely
            // indexed and one unlucky draw would otherwise fail the whole batch at save time.
            //
            // A round draws only what is still missing and asks the database about exactly those,
            // so every code that survives a round has been checked. Re-checking is what the first
            // version of this got wrong: it replaced the clashing codes and then saved them without
            // asking again, which trades a rare failure for a rarer, stranger one.
            for (var round = 0; round < 5 && codes.Count < input.count; round++)
            {
                var wanted = input.count - codes.Count;
                var batch  = new List<string>(wanted);

                while (batch.Count < wanted)
                {
                    var candidate = $"{prefix}-{RandomCodeBody()}";

                    if (drawn.Add(candidate))
                        batch.Add(candidate);
                }

                var taken = await db.Coupons
                   .Where(x => batch.Contains(x.Code))
                   .Select(x => x.Code)
                   .ToListAsync(ct);

                if (taken.Count == 0)
                {
                    codes.AddRange(batch);
                    continue;
                }

                var occupied = taken.ToHashSet(StringComparer.Ordinal);

                codes.AddRange(batch.Where(code => !occupied.Contains(code)));
            }

            if (codes.Count < input.count)
                return new CreateCosmeticCodesResult(false,
                    $"Could not draw {input.count} unused codes under prefix '{prefix}' — pick another prefix",
                    new IonArray<string>([]));

            foreach (var code in codes)
            {
                db.Coupons.Add(new ArgonCouponEntity
                {
                    Id                    = ArgonId.New(),
                    Code                  = code,
                    Description           = $"{item.KindKey}/{item.Slug}",
                    ValidFrom             = input.validFrom,
                    ValidTo               = input.validTo,
                    MaxRedemptions        = 1,
                    RedemptionCount       = 0,
                    IsActive              = true,
                    ReferenceItemEntityId = template.Id,
                });
            }

            // So the card says how the thing can be got. The other modes it already carries are left
            // exactly as they are.
            item.AcquisitionMode |= CosmeticAcquisitionMode.PromoCode;

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("CreateCosmeticCodes", "Cosmetic", input.cosmeticId.ToString(),
                $"{codes.Count} single-use code(s) for key '{input.referenceItemTemplateId}', prefix {prefix}");

            return new CreateCosmeticCodesResult(true, null, new IonArray<string>(codes));
        }
        catch (Exception ex)
        {
            return new CreateCosmeticCodesResult(false, ex.Message, new IonArray<string>([]));
        }
    }

    /// <summary>
    /// What a batch is recognisable by. The operator's prefix if they gave one, the slug otherwise.
    /// </summary>
    private static string NormalizeCodePrefix(string? wanted, string slug)
    {
        var source = string.IsNullOrWhiteSpace(wanted) ? slug : wanted;
        var kept   = new string(source.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        if (kept.Length == 0)
            return "CODE";

        return kept.Length > 8 ? kept[..8] : kept;
    }

    private static string RandomCodeBody()
    {
        var body = new char[CodeBodyLength];

        for (var index = 0; index < body.Length; index++)
            body[index] = CodeAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];

        return new string(body);
    }

    public async Task<IUploadFileResult> BeginUploadCosmeticAsset(
        Guid cosmeticId, CosmeticAssetSlotKind slot, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null || !cosmeticKinds.TryGet(item.KindKey, out var definition))
                return new FailedUploadFile(UploadFileError.INTERNAL_ERROR);

            if (!definition.Assets.ContainsKey(FromWireSlot(slot)))
                return new FailedUploadFile(UploadFileError.INTERNAL_ERROR);

            return await RequestCosmeticUpload(FilePurpose.CosmeticAsset, ct);
        }
        catch (Exception)
        {
            return new FailedUploadFile(UploadFileError.INTERNAL_ERROR);
        }
    }

    public async Task<UserActionResult> CompleteUploadCosmeticAsset(
        Guid cosmeticId, CosmeticAssetSlotKind slot, Guid blobId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

            if (item is null)
                return new UserActionResult(false, "Cosmetic not found");

            if (!cosmeticKinds.TryGet(item.KindKey, out var definition))
                return new UserActionResult(false, $"No cosmetic kind '{item.KindKey}' in this build");

            var domainSlot = FromWireSlot(slot);

            if (!definition.Assets.TryGetValue(domainSlot, out var requirement))
                return new UserActionResult(false, $"Kind '{item.KindKey}' has no {domainSlot} slot");

            var storage = grainFactory.GetGrain<IFileStorageGrain>(UserEntity.SystemUser);
            var file    = await storage.FinalizeUploadAsync(blobId, ct);

            // The media check runs here rather than in the storage grain because only the kind knows
            // what this slot accepts — an image for a badge, a clip for a background. Failing it
            // releases the file rather than leaving it paid for and unreferenced.
            if (!CosmeticAssetMedia.Accepts(requirement.Kind, file.ContentType))
            {
                await storage.DecrementRefAsync(file.FileId, ct);

                return new UserActionResult(false,
                    $"Slot {domainSlot} wants {CosmeticAssetMedia.Describe(requirement.Kind)}, got '{file.ContentType}'");
            }

            if (file.FileSize > requirement.MaxBytes)
            {
                await storage.DecrementRefAsync(file.FileId, ct);

                return new UserActionResult(false,
                    $"Slot {domainSlot} is capped at {requirement.MaxBytes} bytes, got {file.FileSize}");
            }

            // Replacing a slot releases what was there. Without this every re-upload during authoring
            // leaks an object that nothing will ever reference again.
            if (item.AssetFileIds.TryGetValue(domainSlot.ToString(), out var previous)
                && Guid.TryParse(previous, out var previousFileId))
            {
                await storage.DecrementRefAsync(previousFileId, ct);
            }

            // Reassigned rather than mutated: the jsonb comparer decides equality on the serialized
            // form, and a dictionary edited in place on a tracked entity is the lost-write case its
            // own documentation describes.
            item.AssetFileIds = new Dictionary<string, string>(item.AssetFileIds)
            {
                [domainSlot.ToString()] = file.FileId.ToString()
            };

            item.Version += 1;

            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("UploadCosmeticAsset", "Cosmetic", cosmeticId.ToString(),
                $"slot={domainSlot}, fileId={file.FileId}, bytes={file.FileSize}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> GrantCosmetic(
        Guid userId, Guid cosmeticId, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        try
        {
            // Ensure, because a repeat grant from the console is how a timed one is extended: the
            // operator is stating what should be true, not asking whether it already is.
            var grant = await cosmeticGrants.GrantAsync(userId, cosmeticId, CosmeticOwnershipSource.OperatorGrant,
                expiresAt, inventoryItemId: null, CosmeticGrantIntent.Ensure, ct);

            if (grant.Status is CosmeticGrantStatus.UnknownUser)
                return new UserActionResult(false, "User not found");

            if (grant.Status is CosmeticGrantStatus.UnknownCosmetic)
                return new UserActionResult(false, "Cosmetic not found");

            await auditService.LogAsync("GrantCosmetic", "User", userId.ToString(),
                $"cosmetic={cosmeticId}, kind={grant.KindKey}, slug={grant.Slug}, expires={expiresAt}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserActionResult> RevokeCosmetic(Guid userId, Guid cosmeticId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var ownership = await db.CosmeticOwnerships
               .FirstOrDefaultAsync(x => x.UserId == userId && x.CosmeticItemId == cosmeticId && x.RevokedAt == null, ct);

            if (ownership is null)
                return new UserActionResult(false, "That account does not hold this cosmetic");

            ownership.RevokedAt = DateTimeOffset.UtcNow;

            // The equipped row is deliberately left alone. The read path checks ownership, so a
            // revoked cosmetic stops rendering immediately; and if the grant is restored — a refund
            // reversed, a mistake undone — the person is wearing it again rather than having to
            // find it and put it back on.
            await db.SaveChangesAsync(ct);

            await auditService.LogAsync("RevokeCosmetic", "User", userId.ToString(), $"cosmetic={cosmeticId}");

            return new UserActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new UserActionResult(false, ex.Message);
        }
    }

    public async Task<UserCosmeticList> GetUserCosmetics(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var owned = await db.CosmeticOwnerships
           .Where(x => x.UserId == userId)
           .Join(db.Cosmetics, ownership => ownership.CosmeticItemId, item => item.Id,
                (ownership, item) => new
                {
                    Ownership = ownership,
                    Item      = item
                })
           .ToListAsync(ct);

        var now   = DateTimeOffset.UtcNow;
        var items = new List<UserCosmeticInfo>(owned.Count);
        var seen  = new HashSet<Guid>();

        foreach (var row in owned)
        {
            seen.Add(row.Item.Id);

            items.Add(new UserCosmeticInfo(
                row.Item.Id,
                row.Item.KindKey,
                row.Item.Slug,
                row.Item.NameKey,
                ToWireSource(row.Ownership.Source),
                row.Ownership.CreatedAt,
                row.Ownership.ExpiresAt,
                row.Ownership.RevokedAt,
                row.Ownership.IsActiveAt(now),
                false));
        }

        // What the subscription covers, which has no row anywhere and would otherwise be invisible
        // to the person answering a support ticket about why somebody can wear something.
        var hasUltima = await db.Users.AnyAsync(x => x.Id == userId && x.HasActiveUltima, ct);

        if (hasUltima)
        {
            var included = await db.Cosmetics
               .Where(x => x.IsPublished && x.IsEnabled && x.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.UltimaTier))
               .ToListAsync(ct);

            foreach (var item in included)
            {
                if (!seen.Add(item.Id))
                    continue;

                items.Add(new UserCosmeticInfo(
                    item.Id, item.KindKey, item.Slug, item.NameKey,
                    CosmeticOwnershipSourceKind.Subscription,
                    item.CreatedAt, null, null, true, true));
            }
        }

        return new UserCosmeticList(new IonArray<UserCosmeticInfo>(items));
    }

    /// <summary>
    /// A catalogue asset belongs to the platform, not to whoever uploaded it.
    /// </summary>
    /// <remarks>
    /// Keyed to the system account for the same reason inventory reference templates are: an
    /// operator leaves, and a badge everybody wears must not be owned by a deleted account.
    /// </remarks>
    private async Task<IUploadFileResult> RequestCosmeticUpload(FilePurpose purpose, CancellationToken ct)
    {
        var storage = grainFactory.GetGrain<IFileStorageGrain>(UserEntity.SystemUser);
        var upload  = await storage.RequestUploadAsync(new FileUploadRequest(purpose, "application/octet-stream", 0), ct);

        var fields = new IonArray<FormField>(
            upload.Fields.Select(pair => new FormField(pair.Key, pair.Value)).ToList());

        return new SuccessUploadFile(upload.BlobId, upload.Url, fields, upload.TtlSeconds);
    }

    private static CosmeticKindInfo MapKind(CosmeticKindDefinition definition, bool isEnabled, int itemCount, int publishedCount)
    {
        var surfaces = new List<string>();

        foreach (var surface in Enum.GetValues<CosmeticSurface>())
        {
            if (surface is not CosmeticSurface.None && definition.RendersOn(surface))
                surfaces.Add(surface.ToString());
        }

        var scopes = new List<string>();

        foreach (var scope in Enum.GetValues<CosmeticScope>())
        {
            if (definition.SupportsScope(scope))
                scopes.Add(scope.ToString());
        }

        var assets = new List<CosmeticAssetRequirementInfo>(definition.Assets.Count);

        foreach (var requirement in definition.Assets.Values)
        {
            assets.Add(new CosmeticAssetRequirementInfo(
                ToWireSlot(requirement.Slot),
                requirement.Kind.ToString(),
                requirement.MaxBytes,
                requirement.IsRequired));
        }

        return new CosmeticKindInfo(
            definition.Key,
            definition.Description,
            definition.Primitive.ToString(),
            new IonArray<string>(surfaces),
            new IonArray<string>(scopes),
            definition.Stacking.ToString(),
            definition.Layer,
            definition.MaxSlots,
            definition.Board is { } board
                ? new CosmeticBoardCapability(board.MaxWidth, board.MinHeight, board.MaxHeight, board.MinWidth)
                : null,
            definition.Entitlement.ToString(),
            definition.LegacyField is LegacyCosmeticField.None ? null : definition.LegacyField.ToString(),
            definition.FeatureFlagKey,
            isEnabled,
            new IonArray<CosmeticAssetRequirementInfo>(assets),
            itemCount,
            publishedCount);
    }

    private CosmeticSummary MapSummary(CosmeticItemEntity item, string? name)
    {
        var definition = cosmeticKinds.Find(item.KindKey);
        var hasAssets  = true;

        if (definition is not null && item.AssetSource is CosmeticAssetSource.Catalogue)
        {
            foreach (var requirement in definition.RequiredAssets())
            {
                if (!item.AssetFileIds.ContainsKey(requirement.Slot.ToString()))
                {
                    hasAssets = false;
                    break;
                }
            }
        }

        return new CosmeticSummary(
            item.Id,
            item.KindKey,
            item.Slug,
            item.NameKey,
            item.Rarity,
            item.Version,
            item.IsEnabled,
            item.IsPublished,
            new IonArray<CosmeticAcquisitionKind>(ToWireAcquisition(item.AcquisitionMode)),
            hasAssets,
            item.LegacyId,
            item.CreatedAt,
            item.PublishedAt,
            item.ShippedInClientAt,
            item.ShippedInClientBuild,
            name);
    }

    /// <summary>
    /// Whether a slug is one this system can carry everywhere it has to go.
    /// </summary>
    /// <remarks>
    /// A slug is not only a key. It travels on the player wire, a text effect's client file is
    /// named after it, and — since the pack export — <b>it is a directory name in a repository</b>:
    /// the console writes <c>items/{kindKey}/{slug}/</c> into a zip somebody unpacks over their
    /// working tree. A slug of <c>..</c> was accepted before this check, and it turned a perfectly
    /// ordinary export into an archive that escaped the folder it was unpacked into. The import
    /// guards against that too; this is the end where it cannot be created in the first place.
    /// </remarks>
    /// <summary>What <c>CosmeticItemEntity.NameKey</c> is capped at, mirrored here so a derived key fits.</summary>
    private const int NameKeyLimit = 128;

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit];

    private static bool IsUsableSlug(string slug)
    {
        if (slug.Length is < 2 or > 128)
            return false;

        foreach (var character in slug)
        {
            var allowed = character is >= 'a' and <= 'z'
                       || character is >= '0' and <= '9'
                       || character is '-' or '_';

            if (!allowed)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Why an operation on a row whose files are in a client build is refused, or null when it is not.
    /// </summary>
    /// <remarks>
    /// One sentence in one place, because the four callers are four different refusal shapes and the
    /// operator reading any of them needs the same fact: the bytes are in builds that are out there,
    /// and nothing done here can reach them.
    /// <para>
    /// Replacing an asset is deliberately <b>not</b> on this list. A new upload mints a new file id,
    /// every pack misses on it, and every client returns to the CDN by itself — so fixing a picture
    /// costs a fallback rather than a release, and refusing it would only mean the broken one stays.
    /// </para>
    /// </remarks>
    private static string? ShippedRefusal(CosmeticItemEntity item, string what)
        => item.ShippedInClientAt is null
            ? null
            : $"{what} is refused: this cosmetic shipped in client build "
            + $"{item.ShippedInClientBuild ?? "(unnamed)"} on {item.ShippedInClientAt:yyyy-MM-dd}, and its files are "
            + "in builds people are running. Unmark it as shipped first, and only if that is really what you mean.";

    /// <summary>
    /// The templates that mint a key for this cosmetic, by template id.
    /// </summary>
    /// <remarks>
    /// Filtered in memory because the scenarios share one table and <c>is CosmeticScenario</c> is not
    /// a question the database can be asked. Shared by the two calls that care — deletion refuses over
    /// it, unpublishing warns about it — because two copies of "which templates point here" would be
    /// two answers waiting to disagree.
    /// </remarks>
    private static async Task<List<string>> KeyTemplateIdsForAsync(
        ApplicationDbContext db, Guid cosmeticId, CancellationToken ct)
    {
        var templates = await db.Items
           .Where(i => i.IsReference && i.Scenario != null)
           .Include(i => i.Scenario)
           .ToListAsync(ct);

        return templates
           .Where(i => i.Scenario is CosmeticScenario scenario && scenario.CosmeticId == cosmeticId)
           .Select(i => i.TemplateId)
           .ToList();
    }

    private static List<CosmeticAcquisitionKind> ToWireAcquisition(CosmeticAcquisitionMode mode)
    {
        var modes = new List<CosmeticAcquisitionKind>();

        if (mode.HasFlag(CosmeticAcquisitionMode.OperatorGrant))
            modes.Add(CosmeticAcquisitionKind.OperatorGrant);
        if (mode.HasFlag(CosmeticAcquisitionMode.PromoCode))
            modes.Add(CosmeticAcquisitionKind.PromoCode);
        if (mode.HasFlag(CosmeticAcquisitionMode.UltimaTier))
            modes.Add(CosmeticAcquisitionKind.UltimaTier);
        if (mode.HasFlag(CosmeticAcquisitionMode.Purchase))
            modes.Add(CosmeticAcquisitionKind.Purchase);
        if (mode.HasFlag(CosmeticAcquisitionMode.Gift))
            modes.Add(CosmeticAcquisitionKind.Gift);
        if (mode.HasFlag(CosmeticAcquisitionMode.Drop))
            modes.Add(CosmeticAcquisitionKind.Drop);

        return modes;
    }

    private static CosmeticAcquisitionMode FromWireAcquisition(IonArray<CosmeticAcquisitionKind> modes)
    {
        var mode = CosmeticAcquisitionMode.None;

        foreach (var value in modes)
        {
            mode |= FromWireAcquisition(value);
        }

        return mode;
    }

    private static CosmeticAcquisitionMode FromWireAcquisition(CosmeticAcquisitionKind kind) => kind switch
    {
        CosmeticAcquisitionKind.OperatorGrant => CosmeticAcquisitionMode.OperatorGrant,
        CosmeticAcquisitionKind.PromoCode     => CosmeticAcquisitionMode.PromoCode,
        CosmeticAcquisitionKind.UltimaTier    => CosmeticAcquisitionMode.UltimaTier,
        CosmeticAcquisitionKind.Purchase      => CosmeticAcquisitionMode.Purchase,
        CosmeticAcquisitionKind.Gift          => CosmeticAcquisitionMode.Gift,
        CosmeticAcquisitionKind.Drop          => CosmeticAcquisitionMode.Drop,
        _                                     => CosmeticAcquisitionMode.None
    };

    /// <summary>
    /// Every stored flag the wire enum is able to name, and therefore the only part of the mode a
    /// write through the wire is entitled to replace.
    /// </summary>
    /// <remarks>
    /// Folded out of the mapping above rather than written out again, so a member added to one side
    /// cannot leave the mask behind — the whole point of this is that the two agree about what the
    /// wire can say.
    /// </remarks>
    private static readonly CosmeticAcquisitionMode WireAcquisitionFlags =
        Enum.GetValues<CosmeticAcquisitionKind>()
           .Aggregate(CosmeticAcquisitionMode.None, (mask, kind) => mask | FromWireAcquisition(kind));

    private static CosmeticAssetSlotKind ToWireSlot(CosmeticAssetSlot slot) => slot switch
    {
        CosmeticAssetSlot.Poster     => CosmeticAssetSlotKind.Poster,
        CosmeticAssetSlot.Small      => CosmeticAssetSlotKind.Small,
        CosmeticAssetSlot.Secondary  => CosmeticAssetSlotKind.Secondary,
        CosmeticAssetSlot.Tertiary   => CosmeticAssetSlotKind.Tertiary,
        CosmeticAssetSlot.Quaternary => CosmeticAssetSlotKind.Quaternary,
        _                            => CosmeticAssetSlotKind.Primary
    };

    private static CosmeticAssetSlot FromWireSlot(CosmeticAssetSlotKind slot) => slot switch
    {
        CosmeticAssetSlotKind.Poster     => CosmeticAssetSlot.Poster,
        CosmeticAssetSlotKind.Small      => CosmeticAssetSlot.Small,
        CosmeticAssetSlotKind.Secondary  => CosmeticAssetSlot.Secondary,
        CosmeticAssetSlotKind.Tertiary   => CosmeticAssetSlot.Tertiary,
        CosmeticAssetSlotKind.Quaternary => CosmeticAssetSlot.Quaternary,
        _                                => CosmeticAssetSlot.Primary
    };

    private static CosmeticOwnershipSourceKind ToWireSource(CosmeticOwnershipSource source) => source switch
    {
        CosmeticOwnershipSource.PromoCode => CosmeticOwnershipSourceKind.PromoCode,
        CosmeticOwnershipSource.Purchase  => CosmeticOwnershipSourceKind.Purchase,
        CosmeticOwnershipSource.Gift      => CosmeticOwnershipSourceKind.Gift,
        CosmeticOwnershipSource.Item      => CosmeticOwnershipSourceKind.Item,
        _                                 => CosmeticOwnershipSourceKind.OperatorGrant
    };

    private static UltimaPlan ToWirePlan(UltimaTier tier)
        => tier is UltimaTier.Annual ? UltimaPlan.Annual : UltimaPlan.Monthly;

    private static UltimaTier FromWirePlan(UltimaPlan plan)
        => plan is UltimaPlan.Annual ? UltimaTier.Annual : UltimaTier.Monthly;

    private static PublishCosmeticError ToWireRefusal(CosmeticPublicationRefusal refusal) => refusal switch
    {
        CosmeticPublicationRefusal.UnknownKind      => PublishCosmeticError.UnknownKind,
        CosmeticPublicationRefusal.AlreadyPublished => PublishCosmeticError.AlreadyPublished,
        CosmeticPublicationRefusal.MissingAsset     => PublishCosmeticError.MissingAsset,
        CosmeticPublicationRefusal.PayloadInvalid   => PublishCosmeticError.PayloadInvalid,
        CosmeticPublicationRefusal.MissingName      => PublishCosmeticError.MissingName,
        _                                           => PublishCosmeticError.None
    };
}
