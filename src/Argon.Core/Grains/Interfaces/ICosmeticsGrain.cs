namespace Argon.Grains.Interfaces;

/// <summary>A loadout that now exists.</summary>
public sealed record CosmeticLoadoutCreated(Guid LoadoutId);

/// <summary>One card on a profile board, as the wearer left it: its place, its size and its text.</summary>
public sealed record WidgetBoardCard(Guid CosmeticId, string? ContentJson, int X, int Y, int W, int H);

/// <summary>
/// One person's cosmetics: what they may wear, the personas they keep, and what is on each.
/// </summary>
/// <remarks>
/// <para>Keyed by user id. Everything it returns is already resolved for that person — ownership
/// folded in, disabled kinds dropped — so no caller has to know the rules to render the answer.</para>
///
/// <para><b>No Ion union crosses this boundary, deliberately.</b> A union is an interface, and Orleans
/// serializes what a grain returns as JSON without the type information needed to land back on the
/// right case — the call fails at the far end rather than where the mistake is. The codebase already
/// settles this the other way round: <c>IInventoryGrain.RedeemCodeAsync</c> returns a nullable error
/// and <c>InventoryInteractionImpl</c> builds <c>SuccessRedeem</c> or <c>FailedRedeem</c> from it.
/// Same here: plain data out, unions assembled in the Ion service.</para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.ICosmeticsGrain")]
public interface ICosmeticsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetCatalogueAsync))]
    Task<CosmeticCatalogue> GetCatalogueAsync(CancellationToken ct = default);

    [Alias(nameof(GetMyCosmeticsAsync))]
    Task<MyCosmetics> GetMyCosmeticsAsync(CancellationToken ct = default);

    [Alias(nameof(GetMyLoadoutsAsync))]
    Task<CosmeticLoadoutList> GetMyLoadoutsAsync(CancellationToken ct = default);

    [Alias(nameof(CreateLoadoutAsync))]
    Task<Either<CosmeticLoadoutCreated, CosmeticError>> CreateLoadoutAsync(string name, CancellationToken ct = default);

    /// <summary>Null when it worked, which is the shape every action here uses.</summary>
    [Alias(nameof(RenameLoadoutAsync))]
    Task<CosmeticError?> RenameLoadoutAsync(Guid loadoutId, string name, CancellationToken ct = default);

    [Alias(nameof(SetDefaultLoadoutAsync))]
    Task<CosmeticError?> SetDefaultLoadoutAsync(Guid loadoutId, CancellationToken ct = default);

    /// <summary>Sets a look aside, or takes it out again, without touching what it is assigned to.</summary>
    [Alias(nameof(SetLoadoutPausedAsync))]
    Task<CosmeticError?> SetLoadoutPausedAsync(Guid loadoutId, bool paused, CancellationToken ct = default);

    [Alias(nameof(DeleteLoadoutAsync))]
    Task<CosmeticError?> DeleteLoadoutAsync(Guid loadoutId, CancellationToken ct = default);

    /// <summary>The caller's profile as it now reads, so the client need not ask again.</summary>
    [Alias(nameof(EquipAsync))]
    Task<Either<ArgonUserProfile, CosmeticError>> EquipAsync(
        Guid loadoutId, Guid cosmeticId, int slotIndex, string? overridesJson, CancellationToken ct = default);

    /// <summary>
    /// The parts of something worn that only its wearer decides, and the only way a bare kind is put
    /// on at all.
    /// </summary>
    [Alias(nameof(ConfigureCosmeticAsync))]
    Task<Either<ArgonUserProfile, CosmeticError>> ConfigureCosmeticAsync(
        Guid loadoutId, string kindKey, int slotIndex, string? overridesJson, string? contentJson,
        CancellationToken ct = default);

    [Alias(nameof(UnequipAsync))]
    Task<Either<ArgonUserProfile, CosmeticError>> UnequipAsync(
        Guid loadoutId, string kindKey, int slotIndex, CancellationToken ct = default);

    [Alias(nameof(AssignLoadoutToSpaceAsync))]
    Task<CosmeticError?> AssignLoadoutToSpaceAsync(Guid? spaceId, Guid loadoutId, CancellationToken ct = default);

    [Alias(nameof(UnassignLoadoutFromSpaceAsync))]
    Task<CosmeticError?> UnassignLoadoutFromSpaceAsync(Guid? spaceId, CancellationToken ct = default);

    /// <summary>
    /// What a look is called, what it says, and whether it keeps its own picture.
    /// </summary>
    /// <remarks>
    /// Every field is carried on every call, so clearing an override and never setting one are the
    /// same thing said the same way. Null means the account's own value shows through.
    /// </remarks>
    [Alias(nameof(SetLoadoutIdentityAsync))]
    Task<CosmeticError?> SetLoadoutIdentityAsync(
        Guid loadoutId, string? displayName, string? bio, bool keepAvatar, CancellationToken ct = default);

    /// <summary>Points a look at a file the wearer has just uploaded, after it has been moderated.</summary>
    [Alias(nameof(SetLoadoutAvatarAsync))]
    Task<CosmeticError?> SetLoadoutAvatarAsync(Guid loadoutId, Guid blobId, CancellationToken ct = default);

    /// <summary>
    /// Replaces a look's whole profile board: which cards, in what order, how wide, and what each
    /// says. The order is the order of the list.
    /// </summary>
    /// <remarks>
    /// Whole rather than per card, because reordering is every card's position changing at once and
    /// a sequence of edits would leave real intermediate boards for other people to read.
    /// </remarks>
    /// <summary>Accepts a picture for a board card and answers with the file id to put on it.</summary>
    [Alias(nameof(AcceptWidgetPictureAsync))]
    Task<Either<string, CosmeticError>> AcceptWidgetPictureAsync(
        Guid loadoutId, Guid blobId, CancellationToken ct = default);

    [Alias(nameof(SetWidgetBoardAsync))]
    Task<CosmeticError?> SetWidgetBoardAsync(Guid loadoutId, List<WidgetBoardCard> cards, CancellationToken ct = default);

    /// <summary>
    /// What a group of people are wearing here, resolved in a fixed number of queries.
    /// </summary>
    /// <remarks>
    /// Not keyed to the grain's own user: this asks about other people, and the caller's identity is
    /// used only to decide whether they may ask at all.
    /// </remarks>
    [Alias(nameof(GetWornByAsync))]
    Task<List<WornCosmetics>> GetWornByAsync(Guid? spaceId, List<Guid> userIds, CancellationToken ct = default);
}
