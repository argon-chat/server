namespace Argon.Features.Cosmetics;

/// <summary>
/// One kind of profile cosmetic — a background, a badge, an avatar decoration — declared in one file
/// and discovered from the assembly.
/// </summary>
/// <remarks>
/// <para>Implementations must be non-abstract classes. <see cref="Describe"/> is static so the
/// catalogue can be built and validated without constructing anything, the same arrangement
/// <c>IArgonFeature</c> uses.</para>
///
/// <para>Deleting the file is how a kind stops existing. Its key leaves the registry, catalogue rows
/// and equipped rows carrying that key are skipped everywhere by key lookup, and nothing is deleted
/// on their behalf — see <c>CosmeticKindRegistry</c>.</para>
/// </remarks>
public interface ICosmeticKind
{
    static abstract void Describe(ICosmeticKindDescriptor descriptor);
}
