namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

/// <summary>The card itself carries nothing: what it says is what its wearer wrote.</summary>
public sealed class NoteWidgetPayload;

public sealed class NoteWidgetContent
{
    [StringLength(48)]
    public string? Heading { get; set; }

    [StringLength(280)]
    public string? Body { get; set; }
}

/// <summary>
/// A card of the wearer's own words.
/// </summary>
/// <remarks>
/// <para>Deliberately the plainest widget there can be, and here to prove the mechanism rather than
/// to be a feature: a heading and some text, no entity to look up, nothing outside itself. A widget
/// worth having — a favourite game, a shelf of them, statistics — is the same shape with a richer
/// content type and something real behind it.</para>
///
/// <para>What used to sit here was an activity card, and it was a mistake twice over: it repeated the
/// activity line the profile already shows, and it had nothing for its wearer to fill in, which is
/// the one thing that makes a widget a widget.</para>
/// </remarks>
public sealed class NoteWidgetKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("widget.note")
           .Describing("A card of the wearer's own words")
           .Rendering(RenderPrimitive.WidgetSlot)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(500)
           .Stacking(StackingRule.Ordered)
           .MaxSlots(4)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<NoteWidgetPayload>()
           .Board<NoteWidgetContent>(minWidth: 2, maxWidth: 4, minHeight: 5, maxHeight: 12)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
