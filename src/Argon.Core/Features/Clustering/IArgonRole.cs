namespace Argon.Features.Clustering;

/// <summary>
/// A role is a named unit of deployment. One process runs exactly one role.
/// A role declares which grain classes it hosts, which features it enables, and whether
/// it runs an Orleans silo or an Orleans client.
/// </summary>
/// <remarks>
/// Implementations must be non-abstract classes with a public parameterless constructor.
/// <see cref="Id"/> is static so that <c>--roles</c> and <c>--explain</c> can enumerate roles
/// without constructing them.
/// </remarks>
public interface IArgonRole
{
    static abstract ArgonRoleId Id { get; }

    /// <summary>
    /// <c>true</c> — the process runs an Orleans client and hosts no grains;
    /// <c>GrainTypeOptions</c> is left untouched.
    /// <c>false</c> — the process runs a silo.
    /// </summary>
    /// <remarks>
    /// A co-hosted ("hybrid") role is <b>not</b> a client: it runs a silo and serves HTTP from the
    /// same process, using the silo's in-process <see cref="IGrainFactory"/> instead of an external
    /// client connection.
    /// </remarks>
    bool IsClient { get; }

    /// <summary>
    /// Whether this silo exposes the Orleans client gateway endpoint. Replaces the former
    /// dedicated <c>Gateway</c> role. Meaningless when <see cref="IsClient"/> is <c>true</c>.
    /// </summary>
    bool ExposesClusterGateway => false;

    /// <summary>
    /// Whether this silo configures reminders. Checked by validation rule E3 against the
    /// <see cref="IRemindable"/> grains the role hosts.
    /// </summary>
    bool UsesReminders => false;

    string Description => string.Empty;

    void OnGrainReferences(IGrainCollectionRegistry registry)
    {
    }

    void OnFeatures(IArgonFeatureRegistry features)
    {
    }
}
