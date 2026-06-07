namespace Argon.Grains.Interfaces;

using Argon.Core.Entities.Data;

/// <summary>
/// Per-user privacy policy evaluator (GuidKey = the owner's userId). Evaluates the
/// flexible "about-me" rules (who may do X to/about me) for a given viewer + behaviour
/// key, with optional per-space scoping. Caches the owner's rules like
/// <c>FeatureFlagGrain</c>. This is orthogonal to the channel/role entitlement bitmask.
/// </summary>
[Alias("Argon.Grains.Interfaces.IPrivacyPolicyGrain")]
public interface IPrivacyPolicyGrain : IGrainWithGuidKey
{
    /// <summary>
    /// True if <paramref name="viewerId"/> is permitted by the owner's rule for
    /// <paramref name="key"/> (optionally within <paramref name="spaceId"/>). The owner is
    /// always permitted for their own subject.
    /// </summary>
    [Alias(nameof(EvaluateAsync))]
    ValueTask<bool> EvaluateAsync(Guid viewerId, string key, Guid? spaceId);

    /// <summary>Returns the effective rule for a key (space rule preferred over global), or null.</summary>
    [Alias(nameof(GetRuleAsync))]
    ValueTask<PrivacyRuleDto?> GetRuleAsync(string key, Guid? spaceId);

    /// <summary>Upserts the owner's rule for (key, scope). Invalidates the cache.</summary>
    [Alias(nameof(SetRuleAsync))]
    ValueTask SetRuleAsync(PrivacyRuleInput input);

    [Alias(nameof(InvalidateCacheAsync))]
    ValueTask InvalidateCacheAsync();
}

[GenerateSerializer, Immutable]
public sealed record PrivacyRuleDto(
    [property: Id(0)] string Key,
    [property: Id(1)] PrivacyMode Mode,
    [property: Id(2)] Guid? ScopeSpaceId,
    [property: Id(3)] List<Guid> AllowExceptions,
    [property: Id(4)] List<Guid> DenyExceptions);

[GenerateSerializer, Immutable]
public sealed record PrivacyRuleInput(
    [property: Id(0)] string Key,
    [property: Id(1)] PrivacyMode Mode,
    [property: Id(2)] Guid? ScopeSpaceId,
    [property: Id(3)] List<Guid> AllowExceptions,
    [property: Id(4)] List<Guid> DenyExceptions);
