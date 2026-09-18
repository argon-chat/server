namespace Argon.Features.Cosmetics;

using System.Collections.Frozen;
using System.Text.RegularExpressions;

/// <summary>
/// Collects a kind's declaration and turns it into a <see cref="CosmeticKindDefinition"/>, refusing
/// anything that would be ambiguous at render time.
/// </summary>
internal sealed partial class CosmeticKindDescriptor(Type kindType) : ICosmeticKindDescriptor
{
    private readonly Dictionary<CosmeticAssetSlot, CosmeticAssetRequirement> assets = new();
    private readonly Dictionary<string, CosmeticFacetDefinition>            facets = new();

    private string?             key;
    private string?             description;
    private RenderPrimitive?    primitive;
    private CosmeticSurface     surfaces    = CosmeticSurface.None;
    private int                 layer;
    private StackingRule        stacking    = StackingRule.Single;
    private int?                maxSlots;
    private CosmeticScope       scope       = CosmeticScope.Global;
    private Type?               payloadType;
    private CosmeticEntitlement entitlement = CosmeticEntitlement.Owned;
    private LegacyCosmeticField legacyField = LegacyCosmeticField.None;
    private bool                compositional;
    private CosmeticBoardDefinition? board;
    private Type?                    tuningType;
    private bool                     bare;
    private string?             featureFlagKey;

    public ICosmeticKindDescriptor Keyed(string value)
    {
        key = value;
        return this;
    }

    public ICosmeticKindDescriptor Describing(string value)
    {
        description = value;
        return this;
    }

    public ICosmeticKindDescriptor Rendering(RenderPrimitive value)
    {
        primitive = value;
        return this;
    }

    public ICosmeticKindDescriptor On(CosmeticSurface value)
    {
        surfaces |= value;
        return this;
    }

    public ICosmeticKindDescriptor Layer(int value)
    {
        layer = value;
        return this;
    }

    public ICosmeticKindDescriptor Stacking(StackingRule value)
    {
        stacking = value;
        return this;
    }

    public ICosmeticKindDescriptor MaxSlots(int value)
    {
        maxSlots = value;
        return this;
    }

    public ICosmeticKindDescriptor Scoped(CosmeticScope value)
    {
        scope = value;
        return this;
    }

    public ICosmeticKindDescriptor Payload<TPayload>() where TPayload : class
    {
        payloadType = typeof(TPayload);
        return this;
    }

    public ICosmeticKindDescriptor RequiresAsset(CosmeticAssetSlot slot, CosmeticAssetKind kind, long maxBytes)
        => DeclareAsset(slot, kind, maxBytes, true);

    public ICosmeticKindDescriptor AllowsAsset(CosmeticAssetSlot slot, CosmeticAssetKind kind, long maxBytes)
        => DeclareAsset(slot, kind, maxBytes, false);

    public ICosmeticKindDescriptor Entitlement(CosmeticEntitlement value)
    {
        entitlement = value;
        return this;
    }

    public ICosmeticKindDescriptor Facet(string facetId, string optionKindKey)
    {
        if (!facets.TryAdd(facetId, new CosmeticFacetDefinition(facetId, optionKindKey)))
            throw new CosmeticKindDeclarationException(kindType, $"declares axis '{facetId}' twice");

        return this;
    }

    public ICosmeticKindDescriptor Bare()
    {
        bare = true;
        return this;
    }

    public ICosmeticKindDescriptor Tuning<TContent>()
        where TContent : class
    {
        tuningType = typeof(TContent);
        return this;
    }

    public ICosmeticKindDescriptor Board<TContent>(
        int minWidth = 1, int maxWidth = 1, int minHeight = 1, int maxHeight = 4)
        where TContent : class
    {
        if (minWidth < 1 || maxWidth < minWidth || minHeight < 1 || maxHeight < minHeight)
        {
            throw new CosmeticKindDeclarationException(kindType,
                $"asks for a card {minWidth}-{maxWidth} by {minHeight}-{maxHeight}; a card is at least one cell and cannot be smaller than its own minimum");
        }

        if (maxWidth > CosmeticContent.Columns)
        {
            throw new CosmeticKindDeclarationException(kindType,
                $"asks to be {maxWidth} columns wide on a board {CosmeticContent.Columns} columns across");
        }

        board = new CosmeticBoardDefinition(typeof(TContent), minWidth, maxWidth, minHeight, maxHeight);
        return this;
    }

    public ICosmeticKindDescriptor Compositional()
    {
        compositional = true;
        return this;
    }

    public ICosmeticKindDescriptor ProjectsToLegacy(LegacyCosmeticField value)
    {
        legacyField = value;
        return this;
    }

    public ICosmeticKindDescriptor FeatureFlag(string value)
    {
        featureFlagKey = value;
        return this;
    }

    public CosmeticKindDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new CosmeticKindDeclarationException(kindType, "declares no key; call Keyed(\"area.thing\")");
        if (!KeyPattern().IsMatch(key))
            throw new CosmeticKindDeclarationException(kindType, $"key '{key}' must be lowercase dotted, like 'profile.background'");
        if (primitive is null)
            throw new CosmeticKindDeclarationException(kindType, "names no render primitive; call Rendering(...)");
        if (surfaces is CosmeticSurface.None && !compositional)
            throw new CosmeticKindDeclarationException(kindType, "names no surface; call On(...)");

        if (board is not null && primitive is not RenderPrimitive.WidgetSlot)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "declares a board card but does not render as one; call Rendering(RenderPrimitive.WidgetSlot)");
        }

        if (bare && board is not null)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "is bare and also a board card; a card is a catalogue row somebody puts on the board");
        }

        if (bare && assets.Count is not 0)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "is bare and also requires an asset; an asset arrives on a catalogue row and it has none");
        }

        // Both would be two schemas for one column. A card already declares what its wearer fills in.
        if (board is not null && tuningType is not null)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "declares both a board card and tuning; a card's content is already what its wearer fills in");
        }

        if (compositional && facets.Count is not 0)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "is compositional and also declares axes; an option cannot itself be composed");
        }
        if (payloadType is null)
            throw new CosmeticKindDeclarationException(kindType, "declares no payload type; call Payload<T>()");
        if (scope is 0)
            throw new CosmeticKindDeclarationException(kindType, "declares an empty scope; a kind must be equippable somewhere");

        if (stacking is StackingRule.Single && maxSlots is > 1)
        {
            throw new CosmeticKindDeclarationException(kindType,
                $"is Single but asks for {maxSlots} slots; use Stacking(StackingRule.Ordered) or drop MaxSlots");
        }

        if (stacking is StackingRule.Ordered && maxSlots is null)
            throw new CosmeticKindDeclarationException(kindType, "is Ordered but sets no MaxSlots; an unbounded slot count has no render budget");

        return new CosmeticKindDefinition
        {
            KindType        = kindType,
            Key             = key,
            Description     = description,
            Primitive       = primitive.Value,
            Surfaces        = surfaces,
            Layer           = layer,
            Stacking        = stacking,
            MaxSlots        = stacking is StackingRule.Single ? 1 : maxSlots!.Value,
            Scope           = scope,
            PayloadType     = payloadType,
            Entitlement     = entitlement,
            LegacyField     = legacyField,
            IsCompositional = compositional,
            Board           = board,
            AuthoredType    = board?.ContentType ?? tuningType,
            IsBare          = bare,
            FeatureFlagKey  = featureFlagKey ?? DeriveFeatureFlagKey(key),
            Assets          = assets.ToFrozenDictionary(),
            Facets          = facets.ToFrozenDictionary()
        };
    }

    /// <summary>
    /// <c>profile.background</c> becomes <c>af.cosmetics.profile-background.active</c> — the shape
    /// every other flag in the product already has, and the one the client's prefix passthrough
    /// looks for.
    /// </summary>
    /// <remarks>
    /// Flattening dots to dashes is lossy: <c>profile.member-list</c> and <c>profile.member.list</c>
    /// derive the same flag, and one kind would silently gate the other. Keys are allowed dashes
    /// because that is how they read, so the collision is caught where it can be — the registry
    /// refuses two kinds whose derived flags are equal.
    /// </remarks>
    internal static string DeriveFeatureFlagKey(string kindKey)
        => $"af.cosmetics.{kindKey.Replace('.', '-')}.active";

    private ICosmeticKindDescriptor DeclareAsset(CosmeticAssetSlot slot, CosmeticAssetKind kind, long maxBytes, bool isRequired)
    {
        if (maxBytes <= 0)
            throw new CosmeticKindDeclarationException(kindType, $"asset slot {slot} declares a non-positive size limit");

        assets[slot] = new CosmeticAssetRequirement(slot, kind, maxBytes, isRequired);
        return this;
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$")]
    private static partial Regex KeyPattern();
}

public sealed class CosmeticKindDeclarationException(Type kindType, string problem)
    : InvalidOperationException($"Cosmetic kind '{kindType.FullName}' {problem}.");
