namespace Argon.Features.Clustering;

/// <summary>
/// Collects what a role hosts and what it may call.
/// </summary>
/// <remarks>
/// The two edges are deliberately distinct and must not be conflated:
/// <list type="bullet">
/// <item><b>Host</b> — <see cref="AddToRef{T}"/>: this role activates the grain class locally.
/// Feeds <c>GrainTypeOptions.Classes</c>.</item>
/// <item><b>Call</b> — derived by static analysis from the declared roots: code in this role calls
/// a grain interface. A grain call is remote by definition, so the interface must be hosted by
/// <i>some</i> role in the topology, not necessarily by this one.</item>
/// </list>
/// Conflating them is what makes naive dependency closure degenerate into "every role hosts
/// everything".
/// </remarks>
public interface IGrainCollectionRegistry
{
    /// <summary>
    /// This role hosts (activates) the grain class.
    /// </summary>
    void AddToRef<T>() where T : class, IGrain;

    /// <summary>
    /// Registers an analysis root that is not a grain — an Ion service, SignalR hub, controller or
    /// hosted service. Used to derive the call edges of roles that host nothing.
    /// </summary>
    void AddCallRoot<T>();

    /// <summary>
    /// Same as <see cref="AddCallRoot{T}"/> for roots C# refuses as a type argument — a static
    /// class holding extension methods or ambient context, which several of the bot and auth
    /// helpers are.
    /// </summary>
    void AddCallRoot(Type root);

    /// <summary>
    /// Composes another role into this one. Used to build the co-hosted role out of the
    /// individual silo roles rather than duplicating their declarations.
    /// </summary>
    void Include<TRole>() where TRole : IArgonRole, new();

    /// <summary>
    /// Declares that this role invokes the grain at startup — an Orleans startup task, a hosted
    /// service running on boot. Unlike an ordinary call edge this one <b>must</b> be satisfied
    /// locally, because a startup task runs before the rest of the cluster is necessarily up.
    /// Checked by E5.
    /// </summary>
    void AddStartupCall<TInterface>() where TInterface : IGrain;

    /// <summary>
    /// Escape hatch for calls static analysis cannot see — non-generic <c>GetGrain(Type, …)</c>
    /// or reflection-driven dispatch. Adds the interface to this role's call set.
    /// </summary>
    void AddDynamicRef<TInterface>() where TInterface : IGrain;

    /// <summary>
    /// Suppresses E7 for call sites declared by <typeparamref name="TDeclaringType"/>, whose
    /// dynamic dispatch has been reviewed and covered by <see cref="AddDynamicRef{T}"/>.
    /// </summary>
    void AllowUnresolved<TDeclaringType>(string reason);

    /// <summary>
    /// Deliberately accepts a remote hop to a <c>[StatelessWorker]</c> grain this role does not
    /// host, suppressing warning W1 with a recorded reason.
    /// </summary>
    /// <remarks>
    /// The naive fix for W1 — co-host the worker — is sometimes exactly wrong: hosting
    /// <c>ContentModerationGrain</c> on a role would drag the ONNX runtime in with it and defeat
    /// the split. The remote hop is the intended trade and <paramref name="reason"/> records why.
    /// </remarks>
    void AcceptRemote<TInterface>(string reason) where TInterface : IGrain;
}
