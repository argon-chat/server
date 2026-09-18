namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// A picture somebody put on their profile, and a line about it.
/// </summary>
/// <remarks>
/// <para>The file is a file id and never a URL. A card holding an address would be an image request
/// fired from every profile that card is on, at whatever host its owner chose — which is a way to
/// collect the address of everybody who looked at you. It is uploaded through our own storage, gets
/// the same moderation an avatar does, and the client builds the URL from the id the way it does
/// everywhere else.</para>
/// </remarks>
public sealed class PictureWidgetContent : IValidatableCosmeticPayload
{
    /// <summary>What was uploaded. Null is a card waiting for its picture.</summary>
    [StringLength(64)]
    public string? FileId { get; set; }

    [StringLength(80)]
    public string? Caption { get; set; }

    /// <summary>
    /// Whether the file moves, so the client knows which element to mount before fetching it.
    /// </summary>
    [StringLength(8)]
    public string? Kind { get; set; }

    /// <summary>
    /// Whether the picture fills the card or fits inside it.
    /// </summary>
    /// <remarks>
    /// Two words rather than a free string, because this is written into a style on every viewer.
    /// </remarks>
    [StringLength(8)]
    public string? Fit { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (FileId is { Length: > 0 } && !Guid.TryParse(FileId, out _))
            report.Error("fileId is not a file we issued");

        if (Fit is { Length: > 0 } and not ("cover" or "contain"))
            report.Error("fit is either 'cover' or 'contain'");

        if (Kind is { Length: > 0 } and not ("image" or "video"))
            report.Error("kind is either 'image' or 'video'");
    }
}

/// <summary>A card of this kind carries nothing an operator authored: the picture is its wearer's.</summary>
public sealed class PictureWidgetPayload;

/// <summary>
/// The card for things that are not words.
/// </summary>
/// <remarks>
/// <para>A note and a row of tags are both text, so a board of them is a wall of text and there was
/// no way to see how a card of any other shape sits on a profile. This one is a picture and nothing
/// else — it fills its cell, and the caption is the only writing on it.</para>
///
/// <para>Wider and taller than the text cards by default, because a picture read at note size is a
/// thumbnail and the point of putting one on a profile is that it is seen.</para>
/// </remarks>
public sealed class PictureWidgetKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("widget.picture")
           .Describing("A picture card on the profile board")
           .Rendering(RenderPrimitive.WidgetSlot)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(520)
           .Stacking(StackingRule.Ordered)
           .MaxSlots(4)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<PictureWidgetPayload>()
           .Board<PictureWidgetContent>(minWidth: 2, maxWidth: 4, minHeight: 4, maxHeight: 10)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
