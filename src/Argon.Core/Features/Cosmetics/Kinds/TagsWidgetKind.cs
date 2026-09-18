namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

public sealed class TagsWidgetPayload;

public sealed class TagsWidgetContent : IValidatableCosmeticPayload
{
    [StringLength(48)]
    public string? Heading { get; set; }

    public string[]? Tags { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Tags is null)
            return;

        if (Tags.Length > 12)
            report.Error("a card holds at most twelve tags");

        foreach (var tag in Tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 24)
                report.Error("a tag is between one and twenty-four characters");
        }
    }
}

/// <summary>
/// A card of short labels the wearer chose for themselves.
/// </summary>
/// <remarks>
/// The second of the two cards that exist to exercise the mechanism. It is here because its content
/// is a <i>list</i> rather than a string, which is the shape most real widgets have — a shelf of
/// games, a set of interests — and it is the shape that proves an editor can be generated from a
/// declaration rather than written per widget.
/// </remarks>
public sealed class TagsWidgetKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("widget.tags")
           .Describing("A card of short labels the wearer chose")
           .Rendering(RenderPrimitive.WidgetSlot)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(501)
           .Stacking(StackingRule.Ordered)
           .MaxSlots(4)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<TagsWidgetPayload>()
           .Board<TagsWidgetContent>(minWidth: 2, maxWidth: 4, minHeight: 4, maxHeight: 12)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
