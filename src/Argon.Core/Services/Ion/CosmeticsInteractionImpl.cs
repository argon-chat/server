namespace Argon.Services.Ion;

using Argon.Grains.Interfaces;
using ion.runtime;

/// <summary>
/// Profile cosmetics, for the first-party clients.
/// </summary>
/// <remarks>
/// <para>Keyed by the caller rather than by a fresh guid, unlike the inventory service: this grain
/// reads its subject from its own key.</para>
///
/// <para>The unions are built here and not in the grain, which is the same division
/// <c>InventoryInteractionImpl.RedeemCode</c> uses. A union is an interface, and an interface
/// returned from a grain does not survive the hop with enough type information to land on the right
/// case again.</para>
/// </remarks>
public class CosmeticsInteractionImpl : ICosmeticsInteraction
{
    public async Task<CosmeticCatalogue> GetCatalogue(CancellationToken ct = default)
        => await Mine().GetCatalogueAsync(ct);

    public async Task<MyCosmetics> GetMyCosmetics(CancellationToken ct = default)
        => await Mine().GetMyCosmeticsAsync(ct);

    public async Task<CosmeticLoadoutList> GetMyLoadouts(CancellationToken ct = default)
        => await Mine().GetMyLoadoutsAsync(ct);

    public async Task<ICreateLoadoutResult> CreateLoadout(string name, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        var result = await Mine().CreateLoadoutAsync(name, ct);

        return result.IsSuccess
            ? new SuccessCreateLoadout(result.Value.LoadoutId)
            : new FailedCreateLoadout(result.Error);
    }

    public async Task<ILoadoutActionResult> RenameLoadout(Guid loadoutId, string name, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().RenameLoadoutAsync(loadoutId, name, ct));
    }

    public async Task<ILoadoutActionResult> SetDefaultLoadout(Guid loadoutId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().SetDefaultLoadoutAsync(loadoutId, ct));
    }

    public async Task<ILoadoutActionResult> SetLoadoutPaused(Guid loadoutId, bool paused, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().SetLoadoutPausedAsync(loadoutId, paused, ct));
    }

    public async Task<ILoadoutActionResult> DeleteLoadout(Guid loadoutId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().DeleteLoadoutAsync(loadoutId, ct));
    }

    public async Task<IEquipResult> Equip(
        Guid loadoutId, Guid cosmeticId, int slotIndex, string? overridesJson, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().EquipAsync(loadoutId, cosmeticId, slotIndex, overridesJson, ct));
    }

    public async Task<IEquipResult> ConfigureCosmetic(
        Guid loadoutId, string kindKey, int slotIndex, string? overridesJson, string? contentJson,
        CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(
            await Mine().ConfigureCosmeticAsync(loadoutId, kindKey, slotIndex, overridesJson, contentJson, ct));
    }

    public async Task<IEquipResult> Unequip(Guid loadoutId, string kindKey, int slotIndex, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().UnequipAsync(loadoutId, kindKey, slotIndex, ct));
    }

    public async Task<ILoadoutActionResult> AssignLoadoutToSpace(
        Guid? spaceId, Guid loadoutId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().AssignLoadoutToSpaceAsync(spaceId, loadoutId, ct));
    }

    public async Task<ILoadoutActionResult> SetLoadoutIdentity(
        LoadoutIdentityInput input, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().SetLoadoutIdentityAsync(
            input.loadoutId, input.displayName, input.bio, input.keepAvatar, ct));
    }

    /// <summary>
    /// The same upload an account avatar uses, asked for on behalf of a look.
    /// </summary>
    /// <remarks>
    /// The ticket is issued by the user's own file grain, so the file belongs to whoever asked for
    /// it — which is what makes the ownership question unaskable rather than merely answered.
    /// </remarks>
    public async Task<IUploadFileResult> BeginUploadLoadoutAvatar(Guid loadoutId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        var result = await this.GetGrain<IUserGrain>(this.GetUserId())
           .BeginUploadUserFile(UserFileKind.Avatar, ct);

        if (!result.IsSuccess)
            return new FailedUploadFile(result.Error);

        var ticket = result.Value;

        return new SuccessUploadFile(
            ticket.BlobId, ticket.Url, UploadHelpers.ToFormFields(ticket.Fields), ticket.TtlSeconds);
    }

    public async Task<ILoadoutActionResult> CompleteUploadLoadoutAvatar(
        Guid loadoutId, Guid blobId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().SetLoadoutAvatarAsync(loadoutId, blobId, ct));
    }

    /// <summary>
    /// A picture for a board card, taken in the same way and moderated by the same call as an avatar.
    /// </summary>
    public async Task<IUploadFileResult> BeginUploadWidgetPicture(Guid loadoutId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        var result = await this.GetGrain<IUserGrain>(this.GetUserId())
           .BeginUploadUserFile(UserFileKind.ProfilePicture, ct);

        if (!result.IsSuccess)
            return new FailedUploadFile(result.Error);

        var ticket = result.Value;

        return new SuccessUploadFile(
            ticket.BlobId, ticket.Url, UploadHelpers.ToFormFields(ticket.Fields), ticket.TtlSeconds);
    }

    public async Task<IWidgetPictureResult> CompleteUploadWidgetPicture(
        Guid loadoutId, Guid blobId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        var result = await Mine().AcceptWidgetPictureAsync(loadoutId, blobId, ct);

        return result.IsSuccess
            ? new SuccessWidgetPicture(result.Value)
            : new FailedWidgetPicture(result.Error);
    }

    public async Task<ILoadoutActionResult> SetWidgetBoard(
        Guid loadoutId, IonArray<WidgetCard> cards, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        var board = new List<WidgetBoardCard>();

        foreach (var card in cards)
        {
            board.Add(new WidgetBoardCard(card.cosmeticId, card.contentJson, card.x, card.y, card.w, card.h));
        }

        return Describe(await Mine().SetWidgetBoardAsync(loadoutId, board, ct));
    }

    public async Task<ILoadoutActionResult> UnassignLoadoutFromSpace(Guid? spaceId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        return Describe(await Mine().UnassignLoadoutFromSpaceAsync(spaceId, ct));
    }

    /// <summary>
    /// What other people are wearing. Read-only, and gated the way every other read of somebody
    /// else's appearance is: inside a space, membership is the anchor; outside one, this only ever
    /// answers about the caller.
    /// </summary>
    public async Task<IonArray<WornCosmetics>> GetWornBy(
        Guid? spaceId, IonArray<Guid> userIds, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();
        var asked    = userIds.Values.ToList();

        // Without a space there is nothing proving the caller has met these people, and this is a
        // bulk read — exactly the shape that would let somebody walk the directory. The per-person
        // LookupProfile already answers that question properly, through SocialReach.
        if (spaceId is null)
            asked = asked.Where(id => id == callerId).ToList();

        var worn = await this.GetGrain<ICosmeticsGrain>(callerId).GetWornByAsync(spaceId, asked, ct);

        return new IonArray<WornCosmetics>(worn);
    }

    private static ILoadoutActionResult Describe(CosmeticError? error)
        => error is { } failure ? new FailedLoadoutAction(failure) : new SuccessLoadoutAction();

    private static IEquipResult Describe(Either<ArgonUserProfile, CosmeticError> result)
        => result.IsSuccess ? new SuccessEquip(result.Value) : new FailedEquip(result.Error);

    private ICosmeticsGrain Mine() => this.GetGrain<ICosmeticsGrain>(this.GetUserId());
}
