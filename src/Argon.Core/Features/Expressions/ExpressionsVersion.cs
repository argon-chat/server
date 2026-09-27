namespace Argon.Features.Expressions;

using Argon.Services.L1L2;

/// <summary>The content token of a space's expressions: its live packs and their versions, in order.</summary>
/// <remarks>Every change to a pack or its items bumps the pack's version, so the pairs are enough.</remarks>
public static class ExpressionsVersion
{
    public static string Of(IEnumerable<(Guid PackId, long Version)> packs)
        => SpaceReadVersion.Combine(packs.Select(p => $"{p.PackId:N}:{p.Version}").ToArray());

    public static string Of(IEnumerable<ExpressionPack> packs)
        => Of(packs.Select(p => (p.packId, p.version)));

    /// <summary>The token of a space with no packs.</summary>
    public static readonly string Empty = Of(Array.Empty<(Guid, long)>());
}
