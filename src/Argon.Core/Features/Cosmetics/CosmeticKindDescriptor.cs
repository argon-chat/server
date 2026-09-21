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
    private readonly Dictionary<string, CosmeticFacetDefinition>             facets = new();

    private string?                key;
    private string?                description;
    private RenderPrimitive?       primitive;
    private CosmeticSurface        surfaces    = CosmeticSurface.None;
    private int                    layer;
    private StackingRule           stacking    = StackingRule.Single;
    private int?                   maxSlots;
    private CosmeticScope          scope       = CosmeticScope.Global;
    private CosmeticPayloadSchema? payload;
    private CosmeticPayloadSchema? tuning;
    private CosmeticEntitlement    entitlement = CosmeticEntitlement.Owned;
    private bool                   compositional;
    private bool                   bare;
    private string?                featureFlagKey;

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
        payload = CosmeticPayloadSchema.For<TPayload>();
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

    public ICosmeticKindDescriptor Tuning<TContent>() where TContent : class
    {
        tuning = CosmeticPayloadSchema.For<TContent>();
        return this;
    }

    public ICosmeticKindDescriptor Compositional()
    {
        compositional = true;
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
            throw new CosmeticKindDeclarationException(kindType, $"key '{key}' must be lowercase dotted, like 'profile.frame'");
        if (primitive is null)
            throw new CosmeticKindDeclarationException(kindType, "names no render primitive; call Rendering(...)");
        if (surfaces is CosmeticSurface.None && !compositional)
            throw new CosmeticKindDeclarationException(kindType, "names no surface; call On(...)");

        if (bare && assets.Count is not 0)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "is bare and also requires an asset; an asset arrives on a catalogue row and it has none");
        }

        // A payload describes a row. A bare kind has none, so one would be a schema nothing is ever
        // checked against — and a reader would reasonably expect it to be.
        if (bare && payload is not null)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "is bare and also declares a payload; what its wearer sets is Tuning<T>()");
        }

        if (!bare && payload is null)
            throw new CosmeticKindDeclarationException(kindType, "declares no payload type; call Payload<T>()");

        if (compositional && facets.Count is not 0)
        {
            throw new CosmeticKindDeclarationException(kindType,
                "is compositional and also declares axes; an option cannot itself be composed");
        }

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
            Key             = key,
            DeclaredBy      = kindType.FullName ?? kindType.Name,
            Description     = description,
            Primitive       = primitive.Value,
            Surfaces        = surfaces,
            Layer           = layer,
            Stacking        = stacking,
            MaxSlots        = stacking is StackingRule.Single ? 1 : maxSlots!.Value,
            Scope           = scope,
            Payload         = payload,
            Tuning          = tuning,
            Entitlement     = entitlement,
            IsCompositional = compositional,
            IsBare          = bare,
            FeatureFlagKey  = featureFlagKey ?? DeriveFeatureFlagKey(key),
            Assets          = assets.ToFrozenDictionary(),
            Facets          = facets.ToFrozenDictionary()
        };
    }

    /// <summary>
    /// <c>profile.frame</c> becomes <c>af.cosmetics.profile-frame.active</c> — the shape every other
    /// flag in the product already has, and the one the client's prefix passthrough looks for.
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

public sealed class CosmeticKindDeclarationException : InvalidOperationException
{
    /// <summary>Before the declaration was read, when the type is all there is.</summary>
    public CosmeticKindDeclarationException(Type kindType, string problem)
        : base(Say(kindType.FullName ?? kindType.Name, problem)) { }

    /// <summary>After it was read, when the kind is known by the name it declared itself under.</summary>
    public CosmeticKindDeclarationException(CosmeticKindDefinition definition, string problem)
        : base(Say(definition.DeclaredBy, problem)) { }

    private static string Say(string declaredBy, string problem) => $"Cosmetic kind '{declaredBy}' {problem}.";
}
