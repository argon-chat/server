namespace Argon.Features.Cosmetics;

using Argon.Entities;

/// <summary>Why the caller is granting, which is the only thing the two callers disagree about.</summary>
public enum CosmeticGrantIntent
{
    /// <summary>
    /// Make it so. An active row still has its expiry moved, because an operator re-granting with a
    /// new date is asking for precisely that and nothing else.
    /// </summary>
    Ensure,

    /// <summary>
    /// Grant only what is not held. An active row means the key is a second copy of something already
    /// owned, and refusing is what lets the caller leave the item in the inventory.
    /// </summary>
    /// <remarks>
    /// The caller is spending something, so this also declines a catalogue row that is not currently
    /// servable: what is given up has to buy something the person can actually wear.
    /// </remarks>
    OnlyIfMissing
}

/// <summary>What the grant did, or why it could not.</summary>
public enum CosmeticGrantStatus
{
    /// <summary>A new row was written.</summary>
    Granted,

    /// <summary>A lapsed row was carried forward to the new date, and now names this grant as its reason.</summary>
    Extended,

    /// <summary>
    /// The row was already active. Under <see cref="CosmeticGrantIntent.Ensure"/> its expiry was moved
    /// anyway; under <see cref="CosmeticGrantIntent.OnlyIfMissing"/> nothing was written.
    /// </summary>
    AlreadyOwned,

    /// <summary>
    /// The catalogue row exists but is not currently servable, and the caller was spending something
    /// to get it. Distinct from <see cref="UnknownCosmetic"/> because the row is there and may well be
    /// back tomorrow — and distinct from the rest so that the item path can tell "granted nothing" from
    /// "granted something", which is what decides whether the item is kept.
    /// </summary>
    /// <remarks>
    /// Only reachable under <see cref="CosmeticGrantIntent.OnlyIfMissing"/>: an operator granting an
    /// unpublished row is doing it deliberately.
    /// </remarks>
    NotServable,

    UnknownUser,
    UnknownCosmetic
}

/// <summary>
/// The outcome, plus the two catalogue fields every caller wants to name in a log line and would
/// otherwise have to query for a second time.
/// </summary>
public readonly record struct CosmeticGrantResult(CosmeticGrantStatus Status, string? KindKey, string? Slug)
{
    public static CosmeticGrantResult UnknownUser { get; } = new(CosmeticGrantStatus.UnknownUser, null, null);

    public static CosmeticGrantResult UnknownCosmetic { get; } = new(CosmeticGrantStatus.UnknownCosmetic, null, null);
}

/// <summary>
/// The one place an ownership row is born.
/// </summary>
/// <remarks>
/// <para><b>Two callers, one write.</b> An operator grants by hand, in order to make it so; a key item
/// grants by itself, on behalf of whoever opened the case it fell out of. What they do to the database
/// is identical — an active row is already owned, a lapsed row is carried forward rather than
/// duplicated, a revoked row is history and a new row is written beside it. While those rules stood
/// inside <c>AdminConsoleImpl.GrantCosmetic</c>, the second caller would have had to state them again,
/// and repeated rules drift: one copy learns that an expired row is not a duplicate and the other does
/// not, which is the difference between a seasonal cosmetic that can come back and one that has
/// permanently blocked its own way in.</para>
///
/// <para><b>What actually differs is the intent, so it is a parameter.</b> <see cref="CosmeticGrantIntent"/>
/// decides the two questions the paths answer differently: whether an active row is a date to move or a
/// duplicate to refuse, and whether a catalogue row nobody can currently be served is still worth
/// granting. Both come down to the same distinction — an operator is stating what should be true, a
/// player is giving something up — and everything else about the write is identical, which is why it is
/// worth having only one of it.</para>
///
/// <para>The decision itself is <see cref="CosmeticGrantDecision"/>, kept apart because it is a pure
/// function over one row and testable without a database.</para>
/// </remarks>
public sealed class CosmeticGrantService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<CosmeticGrantResult> GrantAsync(
        Guid                    userId,
        Guid                    cosmeticId,
        CosmeticOwnershipSource source,
        DateTimeOffset?         expiresAt,
        Guid?                   inventoryItemId,
        CosmeticGrantIntent     intent,
        CancellationToken       ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await GrantAsync(db, userId, cosmeticId, source, expiresAt, inventoryItemId, intent, ct);
    }

    /// <summary>
    /// The same grant on a context the caller owns, so that it lands inside the caller's transaction.
    /// </summary>
    /// <remarks>
    /// <para>Spending a key is two writes — the item is consumed and the ownership appears — and either
    /// one alone is a support ticket: a key that vanished for nothing, or a cosmetic granted twice by
    /// the retry. The item path opens the transaction, so the ownership has to be written on its
    /// context rather than on one this service opened for itself.</para>
    ///
    /// <para><b>A refusal here obliges the caller to roll back.</b> Every refusal returns before the
    /// save, so it writes nothing and — this is the part that bites — undoes nothing either: whatever
    /// the caller had already tracked on this context is still sitting there, pending. The item path
    /// marks the key deleted before calling in, precisely so that one flush carries both, and its
    /// rollback is what makes a refusal leave the key alone. A caller that used this overload outside
    /// a transaction, or that carried on to its own <c>SaveChangesAsync</c> after a refusal, would
    /// flush that pending delete on its own and eat the item — which is the exact outcome the
    /// duplicate policy exists to prevent.</para>
    /// </remarks>
    public async Task<CosmeticGrantResult> GrantAsync(
        ApplicationDbContext    db,
        Guid                    userId,
        Guid                    cosmeticId,
        CosmeticOwnershipSource source,
        DateTimeOffset?         expiresAt,
        Guid?                   inventoryItemId,
        CosmeticGrantIntent     intent,
        CancellationToken       ct = default)
    {
        if (!await db.Users.AnyAsync(x => x.Id == userId, ct))
            return CosmeticGrantResult.UnknownUser;

        var item = await db.Cosmetics.FirstOrDefaultAsync(x => x.Id == cosmeticId, ct);

        if (item is null)
            return CosmeticGrantResult.UnknownCosmetic;

        var now = DateTimeOffset.UtcNow;

        // Spending is not the same act as being given something. A key is property: the player hands
        // it over for the ownership row, so the row has to be worth having — and a row the catalogue
        // will not serve is not, because the very same four conditions decide what may be equipped and
        // what appears in the picker at all. Unpublish a cosmetic, or simply let its AvailableUntil
        // pass, and without this the key is consumed for something that can never be worn or even
        // seen. An operator granting by hand is the other act: handing a thing out before it is
        // published is deliberate and stays allowed, which is why this asks the intent rather than the
        // row.
        if (intent is CosmeticGrantIntent.OnlyIfMissing && !CosmeticAvailability.IsServable(item, now))
            return new CosmeticGrantResult(CosmeticGrantStatus.NotServable, item.KindKey, item.Slug);

        // Revoked rows are excluded here rather than weighed later: the unique index is filtered the
        // same way, so this finds the at-most-one row that could still be ownership, and any number of
        // revocations sit behind it untouched.
        var existing = await db.CosmeticOwnerships
           .FirstOrDefaultAsync(x => x.UserId == userId && x.CosmeticItemId == cosmeticId && x.RevokedAt == null, ct);

        var outcome = CosmeticGrantDecision.For(existing, now);

        if (outcome is CosmeticGrantOutcome.AlreadyOwned && intent is CosmeticGrantIntent.OnlyIfMissing)
            return new CosmeticGrantResult(CosmeticGrantStatus.AlreadyOwned, item.KindKey, item.Slug);

        if (existing is not null && outcome is not CosmeticGrantOutcome.Granted)
        {
            existing.ExpiresAt = expiresAt;

            if (outcome is CosmeticGrantOutcome.Extended)
            {
                // A revived row is owned because of this grant, and the row's job is to say why the
                // person has the thing now — the history of how they had it before is the audit log's.
                // Left alone, the console would go on naming an operator who handed over something that
                // has since lapsed, and the key actually spent to bring it back would be recorded
                // nowhere. An active row is the other case and is left as it is: nothing about how it
                // began has changed, and rewriting it would relabel somebody's purchase as a hand-out.
                existing.Source          = source;
                existing.InventoryItemId = inventoryItemId;
            }
        }
        else
        {
            db.CosmeticOwnerships.Add(new CosmeticOwnershipEntity
            {
                Id              = ArgonId.New(),
                UserId          = userId,
                CosmeticItemId  = cosmeticId,
                Source          = source,
                ExpiresAt       = expiresAt,
                InventoryItemId = inventoryItemId
            });
        }

        await db.SaveChangesAsync(ct);

        return new CosmeticGrantResult(ToStatus(outcome), item.KindKey, item.Slug);
    }

    private static CosmeticGrantStatus ToStatus(CosmeticGrantOutcome outcome) => outcome switch
    {
        CosmeticGrantOutcome.Granted  => CosmeticGrantStatus.Granted,
        CosmeticGrantOutcome.Extended => CosmeticGrantStatus.Extended,
        _                             => CosmeticGrantStatus.AlreadyOwned
    };
}
