namespace Argon.Features.Clustering;

/// <summary>
/// A named set of roles that together form a complete, working cluster. The unit of validation:
/// "if I run these processes, is every grain someone calls actually hosted by one of them?"
/// </summary>
/// <remarks>
/// Implementations must be non-abstract classes with a public parameterless constructor.
/// </remarks>
public interface IArgonTopology
{
    static abstract string Name { get; }

    static abstract ArgonRoleId[] Roles { get; }

    string Description => string.Empty;
}
