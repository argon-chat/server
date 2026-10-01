namespace Argon.Grains.Interfaces;

/// <summary>What <c>BeginConnect</c> gets back: an authorize URL, or the reason there is none.</summary>
public sealed record BeginConnectOutcome(BeginConnectError Error, string? Url, Guid HandshakeId, DateTimeOffset ExpiresAt)
{
    public static BeginConnectOutcome Refused(BeginConnectError error) => new(error, null, Guid.Empty, default);
}

/// <summary>Which page the browser is shown after a provider redirects back.</summary>
public enum HandshakePageKind
{
    /// <summary>Done; the row is written.</summary>
    Linked,

    /// <summary>
    /// The exchange is parked and the browser has not identified itself yet — the provider's
    /// redirect is cross-site, so the session cookie is not read on it. The page continues to the
    /// resume address on its own.
    /// </summary>
    Continue,

    /// <summary>Parked, and the browser holds no Argon session: sign in, then continue.</summary>
    NeedsSignIn,

    /// <summary>The browser is signed in as somebody other than the account that started this. Nothing linked.</summary>
    WrongUser,

    /// <summary>No handshake behind the state, or the parked result timed out.</summary>
    Expired,

    /// <summary>The person said no at the provider.</summary>
    Denied,

    AlreadyLinkedElsewhere,
    ProviderAlreadyLinked,
    ProviderError,
    Disabled,

    /// <summary>An operator suspended this user's connection of the provider.</summary>
    Suspended
}

/// <param name="SignInUrl">
/// The web client, for the "sign in to finish" page. Carried here so the entry point binds no
/// configuration of its own for this feature.
/// </param>
public sealed record HandshakePage(
    HandshakePageKind Kind,
    ConnectionProvider Provider,
    Guid HandshakeId,
    string? ExternalName,
    string? ExpectedUsername,
    string? SignedInUsername,
    ConnectReturnKind ReturnTo,
    string? SignInUrl = null);

/// <summary>
/// The OAuth handshake: mints and consumes the state, runs the provider's completion, parks the
/// result until the browser has proved who it is, and hands the link to the owner's grain.
/// </summary>
/// <remarks>
/// A stateless worker keyed <c>Guid.Empty</c>: everything it holds is in Redis
/// (<c>ConnectionHandshakeStore</c>), and the one write that must serialise — the row — is the
/// owner's <c>IUserConnectionsGrain</c>'s. The entry point calls this and nothing else; the
/// provider secrets, the adapters and the sealing key stay on the silo.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IConnectionHandshakeGrain")]
public interface IConnectionHandshakeGrain : IGrainWithGuidKey
{
    /// <summary>The providers this deployment has an app registered for. Feature flags are the caller's to apply.</summary>
    [Alias(nameof(GetProvidersAsync))]
    Task<List<ConnectionProviderInfo>> GetProvidersAsync(CancellationToken ct = default);

    [Alias(nameof(BeginAsync))]
    Task<BeginConnectOutcome> BeginAsync(Guid userId, ConnectionProvider provider, ConnectReturnKind returnTo, bool replace, CancellationToken ct = default);

    /// <summary>The provider's redirect: the whole query, and who the browser's web session says it is, if it says.</summary>
    [Alias(nameof(CompleteAsync))]
    Task<HandshakePage> CompleteAsync(ConnectionProvider provider, Dictionary<string, string> query, Guid? browserUserId, CancellationToken ct = default);

    /// <summary>The same-site continuation: the parked result, and who the browser is now.</summary>
    [Alias(nameof(ResumeAsync))]
    Task<HandshakePage> ResumeAsync(Guid handshakeId, Guid? browserUserId, CancellationToken ct = default);
}
