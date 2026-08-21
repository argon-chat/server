namespace ArgonComplexTest;

using ion.runtime;

/// <summary>
/// Stamps the session/device headers every Ion call needs, plus the bearer token of whoever the
/// owning <see cref="TestBase"/> is currently acting as.
/// <para>
/// One instance per caller, never a shared singleton: the token is mutable, so a single instance
/// behind concurrently running fixtures would have them authenticating as each other. The session
/// and machine ids are per instance too, so parallel fixtures look like genuinely distinct clients
/// to the device-history and session-tracking code paths.
/// </para>
/// </summary>
public class DefaultHeaderInterceptor : IIonInterceptor
{
    private readonly Guid    _sessionId = Guid.CreateVersion7();
    private readonly Guid    _machineId = Guid.CreateVersion7();

    /// <summary>
    /// The device id this client claims, verbatim as it goes out in <c>Sec-Carry</c>.
    /// </summary>
    /// <remarks>
    /// Exposed for tests that mint a token by hand: a refresh token carries a hash of the machine
    /// id, and the refresh path refuses one that does not match the caller's. A test that minted
    /// with some other value would be asserting that mismatch rather than whatever it meant to.
    /// </remarks>
    public string MachineId => _machineId.ToString();
    private volatile string? _authToken;

    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        context.RequestItems.Add("Sec-Ref", _sessionId.ToString());
        context.RequestItems.Add("Sec-Ner", "1");
        context.RequestItems.Add("Sec-Carry", _machineId.ToString());

        if (!string.IsNullOrEmpty(_authToken))
            context.RequestItems.Add("Authorization", $"Bearer {_authToken}");

        await next(context, ct);
    }

    public void SetToken(string? t) => _authToken = t;
}
