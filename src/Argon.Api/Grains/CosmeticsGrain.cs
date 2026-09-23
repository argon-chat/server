namespace Argon.Grains;

using Argon.Entities;
using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;
using Grains.Interfaces;
using ion.runtime;
using Services.L1L2;

/// <summary>
/// One person's cosmetics. See <see cref="ICosmeticsGrain"/> for why it is one activation per person.
/// </summary>
/// <remarks>
/// <para><b>Every change ends the same way:</b> this person's cached answer is dropped everywhere,
/// their worn rows are read once, and <c>UserGrain</c> announces the profile with them to every
/// space the person is in. That announcement is the one the product already makes when somebody
/// changes their avatar — this grain never talks to the hub itself, and never builds a profile per
/// space, because without per-space looks the answer is the same in all of them.</para>
///
/// <para>Whether something may be worn is decided here, when it goes on, and asked again when it can
/// have changed: by <see cref="RevalidateAsync"/> as a subscription ends, and on a reminder set for
/// the moment the soonest grant anything worn depends on runs out. The read path never asks — see
/// <see cref="CosmeticWear"/>.</para>
///
/// <para>Every kind this build ships is worn one at a time, so everything goes in slot 0. The column
/// stays for the kinds that will be worn several at once.</para>
/// </remarks>
public sealed class CosmeticsGrain(
    IDbContextFactory<ApplicationDbContext> context,
    CosmeticKindRegistry registry,
    ICosmeticsCache cache,
    ILogger<CosmeticsGrain> logger) : Grain, ICosmeticsGrain, IRemindable
{
    private const int Slot = 0;

    /// <summary>Fires when a grant something is worn on runs out, and takes it off.</summary>
    private const string LapseReminder = "cosmetics-grant-lapse";

    /// <summary>
    /// How soon the lapse reminder fires again when a tick does not finish. A tick that does finish
    /// sets it anew or removes it, so the period is only ever a retry.
    /// </summary>
    private static readonly TimeSpan LapseRetryPeriod = TimeSpan.FromHours(1);

    /// <summary>
    /// The furthest ahead the lapse reminder is set. A grant that runs longer is looked at from there
    /// and the reminder set again — well inside the seven weeks a timer can wait at all.
    /// </summary>
    private static readonly TimeSpan LongestLapseWait = TimeSpan.FromDays(7);

    private Guid UserId => this.GetPrimaryKey();

    private ICosmeticsReadGrain Reader => GrainFactory.GetGrain<ICosmeticsReadGrain>(Guid.Empty);

    public async Task<MyCosmetics> GetMyCosmeticsAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var grants = await ActiveGrantsAsync(ctx);

        var owned = grants
           .Select(grant => new OwnedCosmetic(grant.Key, grant.Value?.UtcDateTime, false))
           .ToList();

        if (await HasPremiumAsync(ctx))
        {
            // What the subscription covers is a question about the catalogue, answered from the one
            // the pickers already read rather than by a query of its own.
            var catalogue = await Reader.GetCatalogueAsync();

            foreach (var item in catalogue.items)
            {
                if (item.ultima && !grants.ContainsKey(item.cosmeticId))
                    owned.Add(new OwnedCosmetic(item.cosmeticId, null, true));
            }
        }

        return new MyCosmetics(new IonArray<OwnedCosmetic>(owned));
    }

    public async Task<IEquipResult> EquipAsync(IWornCosmetic cosmetic)
        => cosmetic switch
        {
            WornItem item         => await EquipItemAsync(item.itemId),
            WornNickname nickname => NicknameStyleKind.FromWire(nickname) is { } look
                ? await ComposeAsync(NicknameStyleKind.Key, look.Options, look.Tuning)
                : Fail(CosmeticError.VALUE_OUT_OF_RANGE),
            _ => Fail(CosmeticError.UNKNOWN_KIND)
        };

    public async Task<IEquipResult> UnequipAsync(string kindKey)
    {
        // Only whether the kind exists. Taking something off is always allowed — a kind switched off
        // is exactly when somebody may want to — so the switch is not asked.
        if (!registry.Contains(kindKey))
            return Fail(CosmeticError.UNKNOWN_KIND);

        await using var ctx = await context.CreateDbContextAsync();

        var removed = await ctx.CosmeticEquips
           .Where(row => row.UserId == UserId && row.KindKey == kindKey && row.SlotIndex == Slot)
           .ExecuteDeleteAsync();

        if (removed is 0)
            return Fail(CosmeticError.NOT_FOUND);

        return await AnnounceAsync();
    }

    public async Task<IonArray<IWornCosmetic>> RevalidateAsync()
    {
        if (await TakeOffLapsedAsync())
            await cache.SignalWornInvalidationAsync(UserId);

        await ArmLapseAsync();

        // Read on this silo, after this silo's own drop: the caller's silo may not have heard it yet.
        return (await Reader.GetWornAsync([UserId]))[UserId];
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != LapseReminder)
            return;

        if (await TakeOffLapsedAsync())
            await AnnounceAsync();
        else
            await ArmLapseAsync();
    }

    public async Task EraseAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();

        await ctx.CosmeticEquips
           .Where(row => row.UserId == UserId)
           .ExecuteDeleteAsync();

        // A grant is an ArgonEntity, and a soft-deleted one is still a row that names this person.
        await ctx.CosmeticOwnerships
           .IgnoreQueryFilters()
           .Where(grant => grant.UserId == UserId)
           .ExecuteDeleteAsync();

        // Nothing is worn any more, so this removes the reminder rather than setting one.
        await ArmLapseAsync();
        await cache.SignalWornInvalidationAsync(UserId);
    }

    /// <summary>
    /// Takes off whatever this person may no longer wear, and says whether anything came off.
    /// </summary>
    private async Task<bool> TakeOffLapsedAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var rows = await ctx.CosmeticEquips
           .Where(row => row.UserId == UserId)
           .ToListAsync();

        if (rows.Count is 0)
            return false;

        var premium = await HasPremiumAsync(ctx);
        var grants  = await ActiveGrantsAsync(ctx);
        var named   = rows.SelectMany(CosmeticWornProjection.Named).Distinct().ToList();

        var items = await ctx.Cosmetics
           .AsNoTracking()
           .Where(item => named.Contains(item.Id))
           .ToDictionaryAsync(item => item.Id);

        bool MayStillWear(Guid itemId)
            => !items.TryGetValue(itemId, out var item)
            || !registry.TryGet(item.KindKey, out var kind)
            || CosmeticWear.MayWear(item, kind, premium, grants.ContainsKey(itemId));

        var changed = false;

        foreach (var row in rows)
        {
            // Only what may no longer be worn comes off. A row whose item is merely unpublished or
            // switched off stays: the read path already hides it, and turning it back on restores it.
            if (row.CosmeticItemId is { } itemId && !MayStillWear(itemId))
            {
                ctx.CosmeticEquips.Remove(row);
                changed = true;
                continue;
            }

            var choices = CosmeticWornProjection.ChoicesOf(row);
            var kept    = choices.Where(choice => MayStillWear(choice.Value)).ToDictionary();

            if (kept.Count == choices.Count)
                continue;

            changed    = true;
            row.Choices = kept.Count is 0 ? null : JsonConvert.SerializeObject(kept);

            if (registry.Find(row.KindKey) is { IsBare: true } && row.Choices is null && row.Tuning is null)
                ctx.CosmeticEquips.Remove(row);
        }

        if (!changed)
            return false;

        await ctx.SaveChangesAsync();

        return true;
    }

    private async Task<IEquipResult> EquipItemAsync(Guid cosmeticId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var item = await ctx.Cosmetics
           .AsNoTracking()
           .FirstOrDefaultAsync(row => row.Id == cosmeticId);

        if (item is null)
            return Fail(CosmeticError.NOT_FOUND);

        if (await CheckKindAsync(item.KindKey) is { } refused)
            return refused;

        var kind = registry.Find(item.KindKey)!;

        // A bare kind is composed, not equipped, and an option is only ever chosen on somebody's axis.
        if (kind.IsBare || kind.IsCompositional || !CosmeticAvailability.IsServable(item, DateTimeOffset.UtcNow))
            return Fail(CosmeticError.ITEM_UNAVAILABLE);

        var grants = await ActiveGrantsAsync(ctx);

        if (!CosmeticWear.MayWear(item, kind, await HasPremiumAsync(ctx), grants.ContainsKey(item.Id)))
            return Fail(CosmeticError.NOT_OWNED);

        var row = await SlotRowAsync(ctx, item.KindKey);

        if (row is null)
        {
            ctx.CosmeticEquips.Add(new CosmeticEquipEntity
            {
                UserId         = UserId,
                KindKey        = item.KindKey,
                SlotIndex      = Slot,
                CosmeticItemId = item.Id,
                UpdatedAt      = DateTimeOffset.UtcNow
            });
        }
        else
        {
            // What was chosen on the slot's axes belongs to the slot, not to the item in it, so
            // swapping the item keeps it.
            row.CosmeticItemId = item.Id;
            row.UpdatedAt      = DateTimeOffset.UtcNow;
        }

        await ctx.SaveChangesAsync();

        return await AnnounceAsync();
    }

    /// <summary>
    /// Puts on a look the wearer composes: the options chosen on the kind's axes, and their own tuning.
    /// It replaces whatever was there; a look with nothing in it takes the kind off.
    /// </summary>
    private async Task<IEquipResult> ComposeAsync(string kindKey, Dictionary<string, Guid> options, object tuning)
    {
        if (await CheckKindAsync(kindKey) is { } refused)
            return refused;

        var kind = registry.Find(kindKey)!;

        // Held to the kind's own schema before it is stored, the same check a stored document gets.
        var tuningJson = kind.Tuning is null ? null : CosmeticJson.Default.Write(tuning);

        if (tuningJson is not null && !kind.ValidateTuning(tuningJson).IsValid)
            return Fail(CosmeticError.VALUE_OUT_OF_RANGE);

        await using var ctx = await context.CreateDbContextAsync();

        if (!await ChoicesMayBeMadeAsync(ctx, kind, options))
            return Fail(CosmeticError.CHOICE_INVALID);

        var said = options.Count > 0 || !IsEmptyTuning(tuningJson);
        var row  = await SlotRowAsync(ctx, kindKey);

        if (!said)
        {
            if (row is not null)
                ctx.CosmeticEquips.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new CosmeticEquipEntity { UserId = UserId, KindKey = kindKey, SlotIndex = Slot };
                ctx.CosmeticEquips.Add(row);
            }

            row.Choices   = options.Count is 0 ? null : JsonConvert.SerializeObject(options);
            row.Tuning    = IsEmptyTuning(tuningJson) ? null : tuningJson;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await ctx.SaveChangesAsync();

        return await AnnounceAsync();
    }

    /// <summary>
    /// Drops this person's cached answer everywhere, sets the lapse reminder for what they now have
    /// on, and has their profile announced with it.
    /// </summary>
    /// <remarks>
    /// The cache is dropped before the read, so the read is a miss and resolves from the rows just
    /// written rather than from anything held.
    /// </remarks>
    private async Task<IEquipResult> AnnounceAsync()
    {
        await cache.SignalWornInvalidationAsync(UserId);
        await ArmLapseAsync();

        var worn    = await Reader.GetWornAsync([UserId]);
        var profile = await GrainFactory.GetGrain<IUserGrain>(UserId).AnnounceProfileAsync(worn[UserId]);

        return new SuccessEquip(profile);
    }

    /// <summary>
    /// Sets the lapse reminder for the soonest grant anything worn depends on, or removes it when
    /// nothing worn depends on one that runs out.
    /// </summary>
    /// <remarks>
    /// <para>Worked out from everything worn rather than from the change just made, because that is
    /// what the reminder guards: swapping a frame can leave it waiting on a grant nothing uses any
    /// more, and a composed name can hang on several.</para>
    ///
    /// <para>Never allowed to fail the caller, for the reason <c>AccountDeletionGrain</c>'s poll is
    /// not: the change before it is committed and announced either way. A reminder that could not be
    /// set costs the automatic lapse only until the next change or revalidation sets it.</para>
    /// </remarks>
    private async Task ArmLapseAsync()
    {
        try
        {
            await using var ctx = await context.CreateDbContextAsync();

            var rows = await ctx.CosmeticEquips
               .AsNoTracking()
               .Where(row => row.UserId == UserId)
               .ToListAsync();

            var named  = rows.SelectMany(CosmeticWornProjection.Named).ToHashSet();
            var grants = await ActiveGrantsAsync(ctx);

            // Null for a permanent grant, and Min skips nulls: only a grant that ends can lapse.
            var soonest = grants
               .Where(grant => named.Contains(grant.Key))
               .Min(grant => grant.Value);

            if (soonest is not { } lapse)
            {
                if (await this.GetReminder(LapseReminder) is { } armed)
                    await this.UnregisterReminder(armed);

                return;
            }

            var wait = lapse - DateTimeOffset.UtcNow;

            await this.RegisterOrUpdateReminder(LapseReminder,
                wait < TimeSpan.Zero ? TimeSpan.Zero : wait > LongestLapseWait ? LongestLapseWait : wait,
                LapseRetryPeriod);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not set the cosmetics lapse reminder for user {UserId}", UserId);
        }
    }

    /// <summary>The checks every change shares: the kind exists and is switched on.</summary>
    private async Task<IEquipResult?> CheckKindAsync(string kindKey)
    {
        if (!registry.Contains(kindKey))
            return Fail(CosmeticError.UNKNOWN_KIND);

        if (!(await Reader.GetEnabledKindsAsync()).Contains(kindKey))
            return Fail(CosmeticError.KIND_DISABLED);

        return null;
    }

    /// <summary>
    /// Whether every option named may be chosen on its axis: it is a row of the axis's kind, it may be
    /// served, and this person may wear it — the rule an item is held to, asked in one query.
    /// </summary>
    private async Task<bool> ChoicesMayBeMadeAsync(
        ApplicationDbContext ctx,
        CosmeticKindDefinition kind,
        Dictionary<string, Guid> options)
    {
        if (options.Count is 0)
            return true;

        if (options.Keys.Any(axis => !kind.Facets.ContainsKey(axis)))
            return false;

        var ids = options.Values.Distinct().ToList();

        var rows = await ctx.Cosmetics
           .AsNoTracking()
           .Where(CosmeticAvailability.ServableAt(DateTimeOffset.UtcNow))
           .Where(item => ids.Contains(item.Id))
           .ToDictionaryAsync(item => item.Id);

        var premium = await HasPremiumAsync(ctx);
        var grants  = await ActiveGrantsAsync(ctx);

        foreach (var (axis, optionId) in options)
        {
            if (!rows.TryGetValue(optionId, out var option)
                || option.KindKey != kind.Facets[axis].OptionKindKey
                || !registry.TryGet(option.KindKey, out var optionKind)
                || !CosmeticWear.MayWear(option, optionKind, premium, grants.ContainsKey(optionId)))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<CosmeticEquipEntity?> SlotRowAsync(ApplicationDbContext ctx, string kindKey)
        => await ctx.CosmeticEquips
           .FirstOrDefaultAsync(row => row.UserId == UserId && row.KindKey == kindKey && row.SlotIndex == Slot);

    private async Task<bool> HasPremiumAsync(ApplicationDbContext ctx)
        => await ctx.Users
           .AsNoTracking()
           .Where(user => user.Id == UserId)
           .Select(user => user.HasActiveUltima)
           .FirstOrDefaultAsync();

    /// <summary>The items this person holds a live grant for, and when each lapses.</summary>
    private async Task<Dictionary<Guid, DateTimeOffset?>> ActiveGrantsAsync(ApplicationDbContext ctx)
    {
        var now = DateTimeOffset.UtcNow;

        return await ctx.CosmeticOwnerships
           .AsNoTracking()
           .Where(grant => grant.UserId == UserId && grant.RevokedAt == null)
           .Where(grant => grant.ExpiresAt == null || grant.ExpiresAt > now)
           .ToDictionaryAsync(grant => grant.CosmeticItemId, grant => grant.ExpiresAt);
    }

    /// <summary>Tuning that says nothing: absent, or an object with no member set.</summary>
    private static bool IsEmptyTuning(string? tuning)
    {
        if (tuning is null)
            return true;

        using var document = System.Text.Json.JsonDocument.Parse(tuning);

        return document.RootElement.ValueKind is System.Text.Json.JsonValueKind.Object
            && document.RootElement.EnumerateObject()
               .All(property => property.Value.ValueKind is System.Text.Json.JsonValueKind.Null);
    }

    private static IEquipResult Fail(CosmeticError error) => new FailedEquip(error);
}
